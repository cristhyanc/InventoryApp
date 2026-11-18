using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformPlans;

/// <summary>
/// The beta Pro grant an approval writes (issue #1258): while
/// <c>Subscriptions:BetaProGrantOnApproval</c> is on, approving a business application also records
/// that business's open-ended Pro grant, in the approval's own transaction, through the same port
/// <see cref="ManageBusinessPlanGrant"/> writes.
///
/// <para><strong>It is one write and it is approval-only.</strong> One business - the one being
/// approved, by the key the approval's transaction read - one <c>BusinessPlanGrant</c> row with
/// source <see cref="ManualPlanGrantSource.Beta"/> and no end date, and the append-only audit row
/// beside it, written in the same <c>SaveChanges</c> by the store. No endpoint, capability or
/// authorization class was added for it: the only caller is
/// <see cref="PlatformAdmin.BusinessApplicationDecisionWriter"/>, which reaches it on an
/// <em>approval</em> alone - a rejection, a deactivation and a reactivation never reach it at
/// all.</para>
///
/// <para><strong>It is one operation with the approval, deliberately.</strong> The write happens
/// inside the transaction <c>MembershipWriteGuard</c> opened, on the same <c>AppDbContext</c>, so
/// either the status change and the grant both commit or neither does: an approved business that was
/// promised Pro and did not get it, and a business on Pro that is still waiting for a decision, are
/// both states this refuses to produce. A failure here therefore rolls the approval back with
/// it.</para>
///
/// <para><strong>Why the log event is a second step.</strong> The audit <em>row</em> is the evidence
/// the issue asks for and is written in the transaction, where it belongs. The structured
/// <c>ILogger</c> event every platform action emits is emitted only <em>after</em> a commit - a log
/// line about a grant that rolled back would be a record of something that never happened - and the
/// commit belongs to the approval, not to this class. So <see cref="WriteAsync"/> returns the entry
/// and the caller hands it back to <see cref="RecordCommitted"/> once the approval has committed.
/// </para>
/// </summary>
public sealed class BetaProGrantOnApproval
{
    private readonly IBetaProGrantOnApprovalFlag _flag;
    private readonly IPlatformPlanGrantStore _store;
    private readonly IPlatformPlanGrantAudit _audit;

    public BetaProGrantOnApproval(
        IBetaProGrantOnApprovalFlag flag,
        IPlatformPlanGrantStore store,
        IPlatformPlanGrantAudit audit)
    {
        _flag = flag;
        _store = store;
        _audit = audit;
    }

    /// <summary>
    /// Writes the beta grant for the business being approved, if this deployment grants one and that
    /// business holds no manual grant already (<see cref="BetaProGrantPolicy.GrantsOnApproval"/>).
    ///
    /// <para>Called from inside the approval's write transaction, after the status change, so the
    /// business row it reads is the one that transaction wrote and the grant is part of the same
    /// commit.</para>
    /// </summary>
    /// <param name="businessId">
    /// The business being approved, taken from the row the approval's transaction read - never from
    /// the route value the request carried.
    /// </param>
    /// <param name="approvedAtUtc">
    /// The approval instant, sampled inside that transaction. The grant applies from it, inclusive.
    /// </param>
    /// <param name="actor">The platform administrator whose approval wrote it.</param>
    /// <param name="cancellationToken">Cancellation for the read and the write.</param>
    /// <returns>
    /// The audit entry to emit once the approval has committed, or <see langword="null"/> when
    /// nothing was written - the setting is off, the business already holds a manual grant, or its
    /// row could not be read at all.
    /// </returns>
    public async Task<PlatformPlanGrantAuditEntry?> WriteAsync(
        int businessId,
        DateTime approvedAtUtc,
        ActorIdentity actor,
        CancellationToken cancellationToken)
    {
        if (!_flag.IsEnabled)
        {
            // Nothing is read while the setting is off, so a deployment that has switched nothing on
            // makes no extra query and approval behaves exactly as it did before this rule existed.
            return null;
        }

        var target = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            // Unreachable from the approval, which has just read and written this very row.
            // Granting nothing is the fail-closed direction if it ever is reached.
            return null;
        }

        var existing = target.Grants
            .Select(grant => new ManualPlanGrantState(grant.Plan, grant.Source, grant.Period))
            .ToList();

        // The setting is asked about twice on purpose: BetaProGrantPolicy is the one place both
        // conditions live, and the short-circuit above exists only so that a deployment with the
        // setting off reads nothing at all.
        if (!BetaProGrantPolicy.GrantsOnApproval(_flag.IsEnabled, existing))
        {
            return null;
        }

        var period = BetaProGrantPolicy.PeriodFrom(approvedAtUtc);

        var write = new PlatformPlanGrantWrite(
            // The key of the row the store read, for the reason every write on this surface uses it:
            // the business is the subject of a decision and never a tenancy scope a request chose.
            target.BusinessId,
            BetaProGrantPolicy.GrantedPlan,
            BetaProGrantPolicy.GrantSource,
            period.StartsAtUtc,
            period.EndsAtUtc,
            GrantReason: BetaProGrantPolicy.GrantReason,
            AuditReason: BetaProGrantPolicy.GrantReason,
            // The first write of this row, counted the way the Super Admin surface counts its own, so
            // the revision its GET reports is the one an end has to present.
            Revision: 1,
            PlatformPlanGrantChange.Created,
            FromPlan: null,
            actor,
            approvedAtUtc);

        await _store.SaveAsync(write, cancellationToken).ConfigureAwait(false);

        return new PlatformPlanGrantAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            write.BusinessId,
            write.Change,
            FromPlan: null,
            ToPlan: write.Plan,
            write.EndsAtUtc,
            write.Revision,
            write.OccurredAtUtc);
    }

    /// <summary>
    /// Emits the one structured log event for a grant that has <strong>committed</strong>, which is
    /// why it is the caller's second call rather than part of the write above.
    /// </summary>
    public void RecordCommitted(PlatformPlanGrantAuditEntry entry) => _audit.Record(entry);
}
