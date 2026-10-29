using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformPlans;

/// <summary>
/// One business's manual plan grant, as the Super Admin reads it (issue #982).
/// </summary>
/// <param name="BusinessId">The business, from the stored row the read found.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Grant">
/// Its complimentary grant, or <see langword="null"/> when it holds none - which is a business with
/// no manual grant, never a business on Free by decision.
/// </param>
/// <param name="Active">
/// Whether that grant entitles anything right now, by the one half-open period rule
/// (<see cref="EntitlementPeriod.Covers"/>) read against the application clock. It is answered here
/// rather than by a presentation layer, so a page never decides for itself whether a grant applies.
/// </param>
public sealed record BusinessPlanGrantView(
    int BusinessId,
    string Name,
    PlatformPlanGrantState? Grant,
    bool Active);

/// <summary>
/// The Super Admin's manual plan grant on one business (issue #982, child of #965, after #966): read
/// it, create or change it, and end it. Three operations over one record, and the whole of what this
/// surface may do.
///
/// <para><strong>What it writes, and what it cannot.</strong> One <c>BusinessPlanGrants</c> row of
/// one business and one audit row beside it, through <see cref="IPlatformPlanGrantStore"/>. No
/// payment, no billing status and no subscription: the business's <c>BusinessBillingSubscription</c>
/// is not on this port, is never read to validate a grant and is never written - the two records
/// stay apart and <see cref="EffectiveEntitlementPolicy"/> remains the one rule that reads both
/// (#965, decided 29 October 2026). No sale, stock, cost, purchase, product, machine, site, document
/// or report value is reachable here either, and nothing is ever deleted: ending a grant records an
/// end instant on it, so the trail of what was granted and why survives.</para>
///
/// <para><strong>The authoritative read, the comparison and the write are one operation.</strong>
/// Each decision opens the store's <c>BEGIN IMMEDIATE</c> transaction, re-reads the business row and
/// its grant <em>inside</em> it, compares the revision the caller presented against the stored one,
/// writes the grant and its audit row in one <c>SaveChanges</c>, and commits. The read before the
/// transaction decides nothing: it exists to answer "no such business" without taking a write lock
/// on the caller's behalf. A stale revision is refused with
/// <see cref="ManualPlanGrantPolicy.StaleRevisionMessage"/> as a conflict, so two Super Admin tabs
/// cannot overwrite each other - the later one is told to reload rather than silently winning.</para>
///
/// <para><strong>The business id is the subject of the decision, not a tenancy scope.</strong> The
/// request carries a denied <c>BusinessScope</c> throughout; the id is checked against the stored
/// business row inside the transaction, and the write is addressed to the key that row carries
/// rather than to the route value (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para><strong>Who may reach it is decided at the boundary, and not here.</strong> The
/// platform-admin policy and its marker authorize the endpoints (class 2), the authorization
/// middleware and <c>BusinessScopeMiddleware</c> each evaluate that policy, a role preview is refused
/// by <c>RolePreviewMiddleware</c> like every other write, and a read-only support session is refused
/// by its own HTTP-method rule and again by <c>BusinessOwnershipEnforcer</c>. A business Owner,
/// Manager or Operator is refused for the one reason every Super Admin surface refuses them: a
/// business role is not platform authority.</para>
/// </summary>
public sealed class ManageBusinessPlanGrant
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformPlanGrantStore _store;
    private readonly IPlatformPlanGrantAudit _audit;
    private readonly IClock _clock;

    public ManageBusinessPlanGrant(
        IAuthenticatedActorAccessor actorAccessor,
        IPlatformPlanGrantStore store,
        IPlatformPlanGrantAudit audit,
        IClock clock)
    {
        _actorAccessor = actorAccessor;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// One business's manual grant as it stands, with the revision a write has to present.
    /// </summary>
    /// <returns>
    /// The business and its grant, or <see langword="null"/> when no business has that key.
    /// </returns>
    /// <exception cref="DomainConflictException">
    /// The business holds more than one complimentary grant, which this surface refuses to choose
    /// between.
    /// </exception>
    public async Task<BusinessPlanGrantView?> GetAsync(int businessId, CancellationToken cancellationToken)
    {
        var target = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);

        return target is null ? null : ViewOf(target, CurrentGrant(target), _clock.UtcNow);
    }

    /// <summary>
    /// Creates the business's manual grant, or changes the one it holds: the plan, an optional end
    /// date, and the reason it was decided for.
    /// </summary>
    /// <param name="businessId">The business being granted - the subject of the decision.</param>
    /// <param name="plan">
    /// The plan's name - <c>Starter</c> or <c>Pro</c> - exactly as the API publishes it. A name
    /// rather than a number, for the reason a role is a name (<c>BusinessRoles.TryParse</c>): a
    /// caller names a plan and never supplies a stored representation. Free is not a grant, and a
    /// name this code does not declare is not a plan; both are refused.
    /// </param>
    /// <param name="endsOnUtc">
    /// The last UTC day the grant applies to, or <see langword="null"/> for no recorded end. It is
    /// stored as the exclusive instant at the start of the following day
    /// (<see cref="ManualPlanGrantPolicy.ExclusiveEndOf"/>).
    /// </param>
    /// <param name="reason">Why it was granted. Required, and recorded.</param>
    /// <param name="revision">
    /// The revision the caller read, and <c>0</c> for a business that holds no grant. Anything else
    /// is a stale view and is refused.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>The grant as it was written, or <see langword="null"/> when no such business exists.</returns>
    /// <exception cref="DomainValidationException">
    /// The plan is not grantable, the reason is missing or too long, or the end date is already over.
    /// Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The revision is stale, or the business holds more than one complimentary grant. Nothing is
    /// written.
    /// </exception>
    public Task<BusinessPlanGrantView?> SetAsync(
        int businessId,
        string? plan,
        DateOnly? endsOnUtc,
        string? reason,
        int revision,
        CancellationToken cancellationToken)
    {
        // Parsed and checked here, before anything is read or locked: only a declared name is a
        // plan, and only Starter and Pro are grants.
        if (!SubscriptionPlans.TryParse(plan, out var requested)
            || !ManualPlanGrantPolicy.IsGrantable(requested))
        {
            throw new DomainValidationException(ManualPlanGrantPolicy.UnsupportedPlanMessage);
        }

        var normalisedReason = RequireReason(reason);

        return DecideAsync(
            businessId,
            (existing, occurredAtUtc) =>
            {
                if (endsOnUtc is { } lastDay && !ManualPlanGrantPolicy.EndsAfter(lastDay, occurredAtUtc))
                {
                    throw new DomainValidationException(ManualPlanGrantPolicy.EndDateInThePastMessage);
                }

                return new Decision(
                    requested,
                    // The grant applies from the instant it was decided, every time it is decided: a
                    // change of plan is a new decision, and dating it from the previous one would
                    // claim the business had the new plan while it had the old one.
                    StartsAtUtc: occurredAtUtc,
                    EndsAtUtc: endsOnUtc is { } day ? ManualPlanGrantPolicy.ExclusiveEndOf(day) : null,
                    GrantReason: normalisedReason,
                    AuditReason: normalisedReason,
                    existing is null ? PlatformPlanGrantChange.Created : PlatformPlanGrantChange.Changed,
                    FromPlan: existing?.Plan,
                    ReportedPlan: requested);
            },
            revision,
            cancellationToken);
    }

    /// <summary>
    /// Ends the business's manual grant now: it stops entitling from this instant, and the business
    /// falls back to whatever else entitles it - its paid subscription, or Free.
    ///
    /// <para>The row is kept, with its plan, its start, its granting reason and its new end instant,
    /// because the trail of what was granted is the point of recording it. Nothing is deleted.</para>
    /// </summary>
    /// <param name="businessId">The business whose grant is being ended.</param>
    /// <param name="reason">Why it was ended. Required, and recorded on the audit row.</param>
    /// <param name="revision">The revision the caller read. A stale one is refused.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>The grant as the end left it, or <see langword="null"/> when no such business exists.</returns>
    /// <exception cref="DomainValidationException">The reason is missing or too long.</exception>
    /// <exception cref="DomainConflictException">
    /// The revision is stale, the business holds no grant that currently applies, or it holds more
    /// than one complimentary grant. Nothing is written.
    /// </exception>
    public Task<BusinessPlanGrantView?> EndAsync(
        int businessId,
        string? reason,
        int revision,
        CancellationToken cancellationToken)
    {
        var normalisedReason = RequireReason(reason);

        return DecideAsync(
            businessId,
            (existing, occurredAtUtc) =>
            {
                if (existing is null || !existing.Period.Covers(occurredAtUtc))
                {
                    // Nothing to end: no grant at all, one that has already ended, or one whose
                    // period never covered anything. Each is refused rather than written over, so an
                    // end never invents an entitlement boundary that did not exist.
                    throw new DomainConflictException(ManualPlanGrantPolicy.NoGrantToEndMessage);
                }

                return new Decision(
                    existing.Plan,
                    existing.Period.StartsAtUtc,
                    EndsAtUtc: occurredAtUtc,
                    // The grant keeps the reason it was granted for; why it was ended is recorded on
                    // the audit row, where the decision it belongs to is.
                    GrantReason: existing.Reason ?? normalisedReason,
                    AuditReason: normalisedReason,
                    PlatformPlanGrantChange.Ended,
                    FromPlan: existing.Plan,
                    // Ended entitles nothing from here, which is not the same answer as "granted
                    // Free", so the audit reports no plan at all rather than the one it stopped.
                    ReportedPlan: null);
            },
            revision,
            cancellationToken);
    }

    /// <summary>
    /// The frame both decisions share: the pre-transaction read that answers "no such business", the
    /// write transaction, the authoritative re-read inside it, the revision comparison, the write of
    /// the grant and its audit row together, the commit, and the one log event afterwards.
    /// </summary>
    private async Task<BusinessPlanGrantView?> DecideAsync(
        int businessId,
        Func<PlatformPlanGrantState?, DateTime, Decision> decide,
        int revision,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var existing = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        PlatformPlanGrantWrite write;
        BusinessPlanGrantView view;

        await using (var transaction = await _store
            .BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            // Re-read with the write lock held. This is the read every rule below is decided on: the
            // business row proves the route value still names a business, and the grant row carries
            // the revision the caller's view is compared against.
            var current = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                // The business row stopped existing between the two reads. No path deletes one, so
                // this is unreachable in practice; answering "not found" is the fail-closed
                // direction.
                return null;
            }

            var grant = CurrentGrant(current);
            if (revision != (grant?.Revision ?? 0))
            {
                throw new DomainConflictException(ManualPlanGrantPolicy.StaleRevisionMessage);
            }

            var occurredAtUtc = _clock.UtcNow;
            var decision = decide(grant, occurredAtUtc);

            write = new PlatformPlanGrantWrite(
                // The key of the row this transaction read, never the route value: that is what
                // makes the id a subject rather than something a request chose.
                current.BusinessId,
                decision.Plan,
                decision.StartsAtUtc,
                decision.EndsAtUtc,
                decision.GrantReason,
                decision.AuditReason,
                Revision: (grant?.Revision ?? 0) + 1,
                decision.Change,
                decision.FromPlan,
                actor,
                occurredAtUtc);

            await _store.SaveAsync(write, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            view = ViewOf(
                current,
                new PlatformPlanGrantState(
                    decision.Plan,
                    new EntitlementPeriod(decision.StartsAtUtc, decision.EndsAtUtc),
                    decision.GrantReason,
                    write.Revision,
                    occurredAtUtc),
                occurredAtUtc);

            _audit.Record(new PlatformPlanGrantAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                current.BusinessId,
                decision.Change,
                decision.FromPlan,
                decision.ReportedPlan,
                decision.EndsAtUtc,
                write.Revision,
                occurredAtUtc));
        }

        return view;
    }

    /// <summary>
    /// The one complimentary grant the business holds, or <see langword="null"/> when it holds none.
    /// More than one is refused rather than chosen between: this surface creates one and only ever
    /// updates it, so two active grants are a state nothing here produced and nothing here will
    /// silently resolve.
    /// </summary>
    private static PlatformPlanGrantState? CurrentGrant(PlatformPlanGrantTarget target) =>
        target.Grants.Count switch
        {
            0 => null,
            1 => target.Grants[0],
            _ => throw new DomainConflictException(ManualPlanGrantPolicy.AmbiguousGrantMessage),
        };

    private static BusinessPlanGrantView ViewOf(
        PlatformPlanGrantTarget target,
        PlatformPlanGrantState? grant,
        DateTime nowUtc) =>
        new(target.BusinessId, target.Name, grant, grant?.Period.Covers(nowUtc) ?? false);

    private static string RequireReason(string? reason)
    {
        var normalised = ManualPlanGrantPolicy.NormaliseReason(reason)
            ?? throw new DomainValidationException(ManualPlanGrantPolicy.ReasonRequiredMessage);

        return ManualPlanGrantPolicy.IsReasonTooLong(normalised)
            ? throw new DomainValidationException(ManualPlanGrantPolicy.ReasonTooLongMessage)
            : normalised;
    }

    /// <summary>
    /// Read before anything else, so a grant can never be committed for a request whose actor the
    /// audit trail could not name. The policy at the boundary has already established that this is
    /// the configured platform administrator; this is who they are.
    /// </summary>
    private ActorIdentity RequireActor() =>
        _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

    /// <summary>
    /// What one decision resolved to, before it is turned into the row that is written: the grant's
    /// new state, which kind of decision it was, and what the audit reports.
    /// </summary>
    private sealed record Decision(
        SubscriptionPlan Plan,
        DateTime StartsAtUtc,
        DateTime? EndsAtUtc,
        string GrantReason,
        string AuditReason,
        PlatformPlanGrantChange Change,
        SubscriptionPlan? FromPlan,
        SubscriptionPlan? ReportedPlan);
}
