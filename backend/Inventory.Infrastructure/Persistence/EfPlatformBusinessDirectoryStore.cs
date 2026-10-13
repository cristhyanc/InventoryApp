using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPlatformBusinessDirectoryStore"/> (issue #544). Like
/// every other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the
/// persistence models it reads and writes.
///
/// <para><strong>It is the whole of the Super Admin's business surface, in one file.</strong> Two
/// tables it reads and writes - businesses and memberships, the structurally non-tenant-owned ones
/// (docs/architecture.md § Tenant ownership) - and one column of a third that it only ever reads.
/// The request it serves carries a <em>denied</em> <c>BusinessScope</c>: the platform administrator
/// has no membership anywhere, nothing resolves a business for them, and the tenant query filters
/// and <c>BusinessOwnershipEnforcer</c> are therefore fully in force over everything else on the
/// context. No product, purchase, sale, document, report or Nayax credential is reachable here.</para>
///
/// <para><strong>The one bypass, and why it is this narrow.</strong>
/// <see cref="ListAsync"/> reports whether each business has stored Nayax credentials, and
/// <c>BusinessNayaxConnection</c> is tenant-owned, so under a denied scope the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of its rows. The
/// alternatives were all worse: an unscoped <c>AppDbContext</c> in a request path would hand this
/// adapter every tenant-owned table at once, a second read-only SQLite connection would be a second
/// cross-business path beside the diagnostics one, and resolving each business into the request
/// scope in turn would make a platform read impersonate every tenant. What is here instead is a
/// single <c>IgnoreQueryFilters</c> statement that projects <em>one</em> column -
/// <c>BusinessId</c>, the ownership key itself, which is not business data - and nothing else: no
/// operator id, no connection status, no key id, and under no circumstances the token ciphertext.
/// It is a reviewed exception recorded in AGENTS.md § Unrestricted access is an explicit opt-in and
/// docs/architecture.md § Super Admin businesses API, and it is the only one: a second
/// <c>IgnoreQueryFilters</c>, a widened projection or a predicate over a tenant-owned table here is
/// a boundary change, not an implementation detail.</para>
///
/// <para><strong>Both writes go through <c>SaveChanges</c> on tracked entities, deliberately.</strong>
/// The central enforcers that validate a business row - <see cref="BusinessStatusEnforcer"/>,
/// <see cref="BusinessTimeZoneEnforcer"/> and <see cref="BusinessAbnEnforcer"/> - and
/// <c>BusinessOwnershipEnforcer</c> itself run there, and a set-based <c>ExecuteUpdate</c> would
/// write straight past all four. They run inside the caller's <c>BEGIN IMMEDIATE</c> transaction and
/// save before returning, because <c>MembershipWriteGuard</c> re-reads afterwards to judge the state
/// the write left behind.</para>
/// </summary>
public sealed class EfPlatformBusinessDirectoryStore : IPlatformBusinessDirectoryStore
{
    private readonly AppDbContext _db;

    public EfPlatformBusinessDirectoryStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlatformBusinessSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var businesses = await _db.Businesses
            .AsNoTracking()
            .Select(business => new
            {
                business.Id,
                business.Name,
                // The stored status exactly as it is, including a value this code does not declare:
                // the directory has to show it rather than present it as something decidable.
                business.Status,
                business.CreatedAtUtc,
                business.AppliedAtUtc,
                business.DecidedAtUtc,
                business.ContactEmail,
                business.Abn,
                ActiveMemberCount = business.Memberships.Count(membership => membership.IsActive),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var configured = await NayaxConfiguredBusinessIdsAsync(cancellationToken).ConfigureAwait(false);

        return businesses
            // Pending first, so the queue the Super Admin is here for leads, then by the business's
            // own key - a total order, so the list does not shuffle between two reads.
            .OrderByDescending(business => business.Status == BusinessStatus.Pending)
            .ThenBy(business => business.Id)
            .Select(business => new PlatformBusinessSummary(
                business.Id,
                business.Name,
                business.Status,
                business.CreatedAtUtc,
                business.AppliedAtUtc,
                business.DecidedAtUtc,
                business.ContactEmail,
                business.Abn,
                business.ActiveMemberCount,
                configured.Contains(business.Id)))
            .ToList();
    }

    /// <inheritdoc />
    public Task<int> CountPendingAsync(CancellationToken cancellationToken) =>
        _db.Businesses.CountAsync(
            business => business.Status == BusinessStatus.Pending,
            cancellationToken);

    /// <inheritdoc />
    public async Task<PlatformBusinessDecisionTarget?> FindDecisionTargetAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        var row = await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId)
            .Select(business => new
            {
                business.Id,
                business.Name,
                business.Status,
                // Every membership of this business, so "exactly one active one" is a fact this
                // read establishes rather than an assumption the caller makes.
                Memberships = business.Memberships
                    .Select(membership => new
                    {
                        membership.Id,
                        membership.DirectoryTenantId,
                        membership.ObjectId,
                        membership.IsActive,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // The applicant is the business's one active membership. A pending business has exactly one
        // - the application creates the business and that membership as one save, and no invite can
        // be redeemed into a business that does not resolve - so this is not a choice between
        // candidates: anything else leaves the applicant unidentified, and the decision refuses
        // rather than picking a row.
        var active = row.Memberships.Where(membership => membership.IsActive).ToList();
        var applicant = active.Count == 1
            ? active[0]
            : null;

        PlatformBusinessApplicant? decisionApplicant = null;
        if (applicant is not null)
        {
            var identified = ActorIdentity.TryCreate(applicant.DirectoryTenantId, applicant.ObjectId, out var identity)
                ? identity
                : null;
            decisionApplicant = new PlatformBusinessApplicant(applicant.Id, identified, applicant.IsActive);
        }

        return new PlatformBusinessDecisionTarget(row.Id, row.Name, row.Status, decisionApplicant);
    }

    /// <inheritdoc />
    public async Task ApproveAsync(int businessId, DateTime decidedAtUtc, CancellationToken cancellationToken)
    {
        var business = await _db.Businesses
            .SingleAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        business.Status = BusinessStatus.Active;
        business.DecidedAtUtc = decidedAtUtc;

        // Nothing else is touched. The rejection reason of an approved business stays as it is
        // (null for every application this path can approve), no membership changes - the
        // applicant's is already active - and no other column of any other table is written.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RejectAsync(
        int businessId,
        string? reason,
        DateTime decidedAtUtc,
        int? deactivateMembershipId,
        CancellationToken cancellationToken)
    {
        var business = await _db.Businesses
            .SingleAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        business.Status = BusinessStatus.Rejected;
        business.RejectionReason = reason;
        business.DecidedAtUtc = decidedAtUtc;

        if (deactivateMembershipId is { } membershipId)
        {
            var membership = await _db.BusinessMemberships
                // Paired with the business id, so a membership key belonging to some other business
                // could not be released by this call even if the caller named one.
                .SingleAsync(
                    candidate => candidate.Id == membershipId && candidate.BusinessId == businessId,
                    cancellationToken)
                .ConfigureAwait(false);

            membership.IsActive = false;

            // Written in the same operation as the state change, so the row never describes a state
            // it reached at some other time (issue #522).
            membership.StatusChangedAtUtc = decidedAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The business ids that have a stored Nayax connection row at all - the one bypass this
    /// adapter makes, and the whole of it.
    ///
    /// <para>It projects the ownership column and nothing else, so what crosses the business
    /// boundary here is the single fact "credentials exist for this business". The credential
    /// columns are not in the projection, are not materialised, and do not appear in the generated
    /// SQL.</para>
    /// </summary>
    private async Task<HashSet<int>> NayaxConfiguredBusinessIdsAsync(CancellationToken cancellationToken)
    {
        var businessIds = await _db.BusinessNayaxConnections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(connection => connection.BusinessId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. businessIds];
    }
}
