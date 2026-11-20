using Inventory.Application.PlatformPlans;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPlatformPlanGrantStore"/> (issue #982, the
/// owner-approved amendment of 29 October 2026). Like every other adapter in this folder it lives
/// beside the <see cref="AppDbContext"/> and the persistence models it reads and writes.
///
/// <para><strong>Three tables, and the whole of what this surface may reach.</strong> It reads the
/// <c>Businesses</c> row the decision names - structurally non-tenant-owned, so no filter applies to
/// it - and reads and writes <c>BusinessPlanGrants</c> and <c>BusinessPlanGrantAudits</c> for that
/// one business. No billing subscription, no sale, stock, cost, purchase, product, machine, site,
/// document or credential is readable or writable here, and no method takes a predicate, a column
/// list, a page or a sort.</para>
///
/// <para><strong>The bypass, and why it is this narrow.</strong> The request it serves is the
/// platform administrator's, who is a member of no business, so it carries a <em>denied</em>
/// <c>BusinessScope</c>: the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of a business's
/// grant rows, and <c>BusinessOwnershipEnforcer</c> refuses every tenant-owned write. This adapter
/// therefore does two things explicitly, and nothing more. Its two statements over
/// <c>BusinessPlanGrants</c> use <c>IgnoreQueryFilters</c>, each pinned to the single business id the
/// decision named <em>and</em> to the two sources this surface administers -
/// <see cref="ManualPlanGrantSource.Complimentary"/> and, since issue #1258,
/// <see cref="ManualPlanGrantSource.Beta"/> - projecting the grant's plan, source, period, reason,
/// revision and instant - this is the fourth reviewed
/// <c>IgnoreQueryFilters</c> path in a request (AGENTS.md § There are exactly four reviewed
/// IgnoreQueryFilters paths), and the alternatives were the ones issue #544 already rejected: an
/// unscoped context in a request path would hand one adapter every tenant-owned table at once, and
/// resolving each business into the request scope would make a platform decision impersonate a
/// tenant. And the write takes <c>AppDbContext.BeginPlanGrantWrite</c> around its single
/// <c>SaveChanges</c>, which permits exactly one business's grant row and the audit row beside it.
/// A wider projection, a third statement, a predicate over another tenant-owned table, or a lease
/// taken anywhere else is a boundary change needing its own reviewed issue - not an implementation
/// detail.</para>
///
/// <para><strong>The grant and its audit row are one save.</strong> They are written in the same
/// <c>SaveChanges</c>, inside the caller's <c>BEGIN IMMEDIATE</c> transaction, so there is no moment
/// at which a database holds a granted plan with no record of who gave it, or a record of a decision
/// that was rolled back.</para>
///
/// <para><strong>The grandfathered grant is never touched.</strong> Every statement here filters on
/// the administered sources, so the row issue #966's command wrote - its plan, its start, its end and
/// its revision - is not read by this surface, not compared against, and not writable through it.
/// Ending an administered grant records an end instant on it; nothing here deletes a row.</para>
///
/// <para><strong>The beta grant of issue #1258 is written through this same adapter and by nothing
/// else.</strong> The approval of a business application calls <see cref="SaveAsync"/> inside its own
/// write transaction, so the grant and its audit row join the approval's commit; the row is created
/// with the source the write carries, and an existing row keeps the source it was created with. The
/// lease, the statements and the tables are unchanged by that caller.</para>
/// </summary>
public sealed class EfPlatformPlanGrantStore : IPlatformPlanGrantStore
{
    private readonly AppDbContext _db;

    public EfPlatformPlanGrantStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PlatformPlanGrantTarget?> FindAsync(int businessId, CancellationToken cancellationToken)
    {
        // The business row first, and not as proof of tenancy: it is what makes the route value the
        // subject of a decision rather than something a request chose, and an id matching no row is
        // simply not found.
        var business = await _db.Businesses
            .AsNoTracking()
            .Where(candidate => candidate.Id == businessId)
            .Select(candidate => new { candidate.Id, candidate.Name })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return null;
        }

        var grants = await AdministeredGrants()
            .AsNoTracking()
            .Where(grant => grant.BusinessId == businessId)
            .OrderBy(grant => grant.Id)
            .Select(grant => new
            {
                grant.Plan,
                grant.Source,
                grant.StartsAtUtc,
                grant.EndsAtUtc,
                grant.Reason,
                grant.Revision,
                grant.GrantedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PlatformPlanGrantTarget(
            business.Id,
            business.Name,
            grants.ConvertAll(grant => new PlatformPlanGrantState(
                // Reported exactly as stored, an undeclared plan included: which plan a business is
                // entitled to is EffectiveEntitlementPolicy's decision, and which one may be granted
                // is ManualPlanGrantPolicy's. An adapter decides neither.
                grant.Plan,
                grant.Source,
                new EntitlementPeriod(grant.StartsAtUtc, grant.EndsAtUtc),
                grant.Reason,
                grant.Revision,
                grant.GrantedAtUtc)));
    }

    /// <inheritdoc />
    public async Task<IPlatformPlanGrantWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken) =>
        new WriteTransaction(
            await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task SaveAsync(PlatformPlanGrantWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        // Tracked, because the update below is made on this very row and goes through SaveChanges -
        // where BusinessOwnershipEnforcer and every other central enforcer runs. A set-based
        // ExecuteUpdate would write straight past all of them.
        var grant = await AdministeredGrants()
            .Where(candidate => candidate.BusinessId == write.BusinessId)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            _db.BusinessPlanGrants.Add(new BusinessPlanGrant
            {
                Plan = write.Plan,

                // The source the decision was made for: a Super Admin's complimentary grant, or the
                // beta grant an approval wrote (issue #1258). It is set here and nowhere else -
                // the update below leaves an existing row's source alone - so why a business holds
                // the plan is recorded once, when the grant is created.
                Source = write.Source,
                StartsAtUtc = write.StartsAtUtc,
                EndsAtUtc = write.EndsAtUtc,
                Reason = write.GrantReason,
                GrantedAtUtc = write.OccurredAtUtc,
                Revision = write.Revision,

                // BusinessId is deliberately left unset: the lease below stamps it, through the same
                // central enforcer every ordinary insert goes through, so this adapter never assigns
                // an owner itself.
            });
        }
        else
        {
            grant.Plan = write.Plan;
            grant.StartsAtUtc = write.StartsAtUtc;
            grant.EndsAtUtc = write.EndsAtUtc;
            grant.Reason = write.GrantReason;
            grant.GrantedAtUtc = write.OccurredAtUtc;
            grant.Revision = write.Revision;
        }

        _db.BusinessPlanGrantAudits.Add(new BusinessPlanGrantAudit
        {
            Change = write.Change,
            FromPlan = write.FromPlan,
            ToPlan = write.Change == PlatformPlanGrantChange.Ended ? null : write.Plan,
            EndsAtUtc = write.EndsAtUtc,
            Reason = write.AuditReason,
            Revision = write.Revision,
            ActorDirectoryTenantId = write.Actor.DirectoryTenantId,
            ActorObjectId = write.Actor.ObjectId,
            OccurredAtUtc = write.OccurredAtUtc,
        });

        // One save, one lease: the grant and its audit row reach the database together or not at
        // all, and the window closes before this method returns.
        using (_db.BeginPlanGrantWrite(write.BusinessId))
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The business's grants <strong>this surface administers</strong>, and the one place the filter
    /// bypass is written.
    ///
    /// <para>Both callers read through here, so "pinned to the administered sources, and the
    /// business id supplied beside it" is one statement rather than two that could drift. Those
    /// sources are the complimentary grant of issue #982 and the beta grant of issue #1258 - which is
    /// written through this very adapter and which a Super Admin has to be able to see and end on the
    /// plan view. The <em>grandfathered</em> grant is still excluded by the predicate, which is what
    /// keeps it unreadable and unwritable through this surface.</para>
    /// </summary>
    private IQueryable<BusinessPlanGrant> AdministeredGrants() =>
        _db.BusinessPlanGrants
            .IgnoreQueryFilters()
            .Where(grant => grant.Source == ManualPlanGrantSource.Complimentary
                || grant.Source == ManualPlanGrantSource.Beta);

    /// <summary>
    /// The caller's transaction, over the shared <see cref="SqliteWriteLockTransaction"/> every write
    /// path in this folder opens: <c>BEGIN IMMEDIATE</c>, so the write lock is held before the read
    /// the revision comparison is made on.
    /// </summary>
    private sealed class WriteTransaction : IPlatformPlanGrantWriteTransaction
    {
        private readonly SqliteWriteLockTransaction _transaction;

        public WriteTransaction(SqliteWriteLockTransaction transaction)
        {
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
