using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPlatformBusinessDirectoryStore"/> (issue #544, extended
/// with the lifecycle transitions of issue #545). Like every other adapter in this folder it lives
/// beside the <see cref="AppDbContext"/> and the persistence models it reads and writes.
///
/// <para><strong>It is the whole of the Super Admin's business surface, in one file.</strong> Two
/// tables it reads and writes - businesses and memberships, the structurally non-tenant-owned ones
/// (docs/architecture.md § Tenant ownership) - and two columns of a third that it only ever reads.
/// The request it serves carries a <em>denied</em> <c>BusinessScope</c>: the platform administrator
/// has no membership anywhere, nothing resolves a business for them, and the tenant query filters
/// and <c>BusinessOwnershipEnforcer</c> are therefore fully in force over everything else on the
/// context. No product, purchase, sale, document, report or Nayax credential is reachable here.</para>
///
/// <para><strong>The one bypass, and why it is this narrow.</strong>
/// <see cref="ListAsync"/> reports whether each business has stored Nayax credentials and how its own
/// last test left them, and <c>BusinessNayaxConnection</c> is tenant-owned, so under a denied scope
/// the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of its rows. The
/// alternatives were all worse: an unscoped <c>AppDbContext</c> in a request path would hand this
/// adapter every tenant-owned table at once, a second read-only SQLite connection would be a second
/// cross-business path beside the diagnostics one, and resolving each business into the request
/// scope in turn would make a platform read impersonate every tenant. What is here instead is a
/// single <c>IgnoreQueryFilters</c> statement that projects <em>two</em> columns -
/// <c>BusinessId</c>, the ownership key itself, which is not business data, and <c>Status</c>, the
/// stored connection status (issue #983, the owner-approved widening of this projection by exactly
/// that status) - and nothing else: no operator id, no key id, no credential revision, no
/// last-tested instant, and under no circumstances the token ciphertext.
/// It is a reviewed exception recorded in AGENTS.md § Unrestricted access is an explicit opt-in and
/// docs/architecture.md § Super Admin businesses API, and it is the only one: a second
/// <c>IgnoreQueryFilters</c>, a further widened projection or a predicate over a tenant-owned table
/// here is a boundary change, not an implementation detail.</para>
///
/// <para><strong>Every write goes through <c>SaveChanges</c> on tracked entities, deliberately.</strong>
/// The central enforcers that validate a business row - <see cref="BusinessStatusEnforcer"/>,
/// <see cref="BusinessTimeZoneEnforcer"/> and <see cref="BusinessAbnEnforcer"/> - and
/// <c>BusinessOwnershipEnforcer</c> itself run there, and a set-based <c>ExecuteUpdate</c> would
/// write straight past all four. They run inside the caller's <c>BEGIN IMMEDIATE</c> transaction and
/// save before returning, because <c>MembershipWriteGuard</c> re-reads afterwards to judge the state
/// the write left behind and because a deactivation's own next statement has to see the status it
/// just wrote.</para>
///
/// <para><strong>What the lifecycle transitions may write is one column.</strong> A deactivation and
/// a reactivation write <c>Businesses.Status</c> and nothing else - no decision instant, no rejection
/// reason, no membership, no invite - and a release writes one membership's <c>IsActive</c> and
/// <c>StatusChangedAtUtc</c> together, paired with the business it must belong to. The deactivation's
/// invite cancellation is deliberately not here: it is issue #531's single conditional statement on
/// its own port, which this adapter has no part in.</para>
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
                // The state the application recorded (issue #933): one more column of the same
                // fixed set of the business's own published details, never a tenant-owned value.
                business.State,
                ActiveMemberCount = business.Memberships.Count(membership => membership.IsActive),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var connectionStatuses = await NayaxConnectionStatusesAsync(cancellationToken).ConfigureAwait(false);

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
                business.State,
                business.ActiveMemberCount,
                connectionStatuses.ContainsKey(business.Id),
                // A business with no connection row is NotConfigured, which is exactly what the
                // absence of that row means (issue #983) - never a guess, and never a status a
                // business with nothing stored could be presented as having.
                connectionStatuses.TryGetValue(business.Id, out var status)
                    ? status
                    : NayaxConnectionStatus.NotConfigured))
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

    /// <inheritdoc />
    public async Task<PlatformBusinessLifecycleTarget?> FindLifecycleTargetAsync(
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
                // Every membership of this business, active and inactive alike: a reactivation has
                // to judge whether an active Owner is left, and a release has to find the one
                // membership it names. The Entra pair is deliberately not projected - no decision
                // here is made from an identity, so it is not read.
                Members = business.Memberships
                    .OrderBy(membership => membership.Id)
                    .Select(membership => new
                    {
                        membership.Id,
                        membership.DisplayName,
                        membership.Role,
                        membership.IsActive,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new PlatformBusinessLifecycleTarget(
                row.Id,
                row.Name,
                row.Status,
                [.. row.Members.Select(member => new PlatformBusinessMember(
                    member.Id,
                    member.DisplayName,
                    member.Role,
                    member.IsActive))]);
    }

    /// <inheritdoc />
    public Task DeactivateAsync(int businessId, CancellationToken cancellationToken) =>
        SetStatusAsync(businessId, BusinessStatus.Deactivated, cancellationToken);

    /// <inheritdoc />
    public Task ReactivateAsync(int businessId, CancellationToken cancellationToken) =>
        SetStatusAsync(businessId, BusinessStatus.Active, cancellationToken);

    /// <inheritdoc />
    public async Task ReleaseMemberAsync(
        int businessId,
        int membershipId,
        DateTime releasedAtUtc,
        CancellationToken cancellationToken)
    {
        var membership = await _db.BusinessMemberships
            // Paired with the business id, exactly as the rejection's release is, so a membership
            // key belonging to some other business could not be released by this call even if the
            // caller named one.
            .SingleAsync(
                candidate => candidate.Id == membershipId && candidate.BusinessId == businessId,
                cancellationToken)
            .ConfigureAwait(false);

        membership.IsActive = false;

        // Written in the same operation as the state change, so the row never describes a state it
        // reached at some other time (issue #522).
        membership.StatusChangedAtUtc = releasedAtUtc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one write both lifecycle transitions make: a business's status column, and nothing else
    /// anywhere (issue #545).
    ///
    /// <para>No decision instant and no rejection reason: those record what became of an
    /// <em>application</em> (issue #540), and a business being switched off or on is not a decision
    /// on one. No membership and no invite either - the deactivation's invite cancellation is its
    /// own statement on its own port, and a reactivation touches neither.</para>
    ///
    /// <para>Through <c>SaveChanges</c> on the tracked entity, like the two decisions above, so
    /// <see cref="BusinessStatusEnforcer"/>, <see cref="BusinessTimeZoneEnforcer"/>,
    /// <see cref="BusinessAbnEnforcer"/> and <c>BusinessOwnershipEnforcer</c> all run; it saves
    /// before returning because the caller's transaction continues with statements that must see
    /// this write.</para>
    /// </summary>
    private async Task SetStatusAsync(
        int businessId,
        BusinessStatus status,
        CancellationToken cancellationToken)
    {
        var business = await _db.Businesses
            .SingleAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        business.Status = status;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The stored connection status of every business that has a Nayax connection row at all - the
    /// one bypass this adapter makes, and the whole of it.
    ///
    /// <para>It projects the ownership column and the status column, and nothing else, so what
    /// crosses the business boundary here is "credentials exist for this business" and "this is how
    /// its last test left them" (issue #983, the owner-approved widening of this projection by
    /// exactly that stored status). The credential columns are not in the projection, are not
    /// materialised and do not appear in the generated SQL: no operator id, no key id, no credential
    /// revision, no last-tested instant and under no circumstances the token ciphertext.</para>
    ///
    /// <para>A business has at most one connection row, enforced by a unique index on the ownership
    /// column, so the status is read per business rather than chosen between candidates.</para>
    /// </summary>
    private async Task<Dictionary<int, NayaxConnectionStatus>> NayaxConnectionStatusesAsync(
        CancellationToken cancellationToken)
    {
        var connections = await _db.BusinessNayaxConnections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(connection => new { connection.BusinessId, connection.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return connections.ToDictionary(
            connection => connection.BusinessId,
            connection => connection.Status);
    }
}
