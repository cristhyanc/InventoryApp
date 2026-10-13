namespace Inventory.Domain.Tenancy;

/// <summary>
/// The one rule that turns an identity's membership records into its <see cref="AccountState"/>
/// (issue #523), with the precedence agreed in #328: first match wins.
///
/// <list type="number">
///   <item><see cref="AccountState.MembershipAmbiguous"/>: more than one active membership.</item>
///   <item><see cref="AccountState.Member"/>: one active membership in an active business.</item>
///   <item><see cref="AccountState.ApplicationPending"/>: one active membership in a pending
///   business (<see cref="BusinessStatus.Pending"/>, issue #540).</item>
///   <item><see cref="AccountState.BusinessDeactivated"/>: one active membership in a business that
///   is neither active nor pending.</item>
///   <item>No active membership: the most recent inactive record decides - an inactive membership
///   in a <see cref="BusinessStatus.Rejected"/> business gives
///   <see cref="AccountState.ApplicationRejected"/> with that business's recorded reason, any
///   other gives <see cref="AccountState.MembershipRevoked"/>.</item>
///   <item><see cref="AccountState.NoMembership"/>: no rows.</item>
/// </list>
///
/// <para>The ordering is the whole rule. A person's history may hold any number of revoked rows,
/// and the one thing that must never happen is an old revocation describing an account that works
/// today: the active membership is examined before the inactive ones, not merged with them.</para>
///
/// <para><strong>Step 4 is the fail-closed step.</strong> It is written as "neither active nor
/// pending" rather than as a list of the remaining statuses, so a status this code does not
/// declare - and an active membership in a <see cref="BusinessStatus.Rejected"/> business, which
/// the approval workflow does not produce because rejecting an application revokes its memberships
/// in the same operation - report the state that promises nothing instead of falling through to a
/// screen that claims the account works.</para>
///
/// <para><strong>Step 5 orders the inactive records.</strong> Two inactive records can now give
/// different states, so which one is most recent decides: the record with the latest
/// <see cref="ActorBusinessMembership.StatusChangedAtUtc"/> (issue #522's membership column). An
/// old rejection must never outrank a newer revocation. A tie - two records whose state changed at
/// the very same instant - resolves towards <see cref="AccountState.MembershipRevoked"/>, which is
/// the state that claims less about what happened, so the answer is deterministic rather than
/// dependent on the order persistence returned the rows in.</para>
///
/// <para><strong>This rule grants nothing.</strong> It is a description of the caller's own
/// account, reported only to that caller.
/// <see cref="BusinessMembershipResolutionPolicy"/> stays the only rule that decides access, and a
/// state is never consulted by it - which is also why a current membership carrying a role this
/// code does not declare is still <see cref="AccountState.Member"/> here while every business
/// endpoint refuses it: the account exists, and the undeclared role is an operator's problem, not
/// a different account state.</para>
/// </summary>
public static class AccountStatePolicy
{
    public static AccountStateDecision Determine(IReadOnlyCollection<ActorBusinessMembership>? memberships)
    {
        if (memberships is null || memberships.Count == 0)
        {
            return State(AccountState.NoMembership);
        }

        var active = memberships.Where(membership => membership.IsActive).ToList();

        if (active.Count > 1)
        {
            return State(AccountState.MembershipAmbiguous);
        }

        if (active.Count == 1)
        {
            return State(active[0].BusinessStatus switch
            {
                BusinessStatus.Active => AccountState.Member,
                BusinessStatus.Pending => AccountState.ApplicationPending,
                _ => AccountState.BusinessDeactivated,
            });
        }

        // Every record is inactive: the account held a membership and no longer does. The most
        // recent record is the one that describes how that happened, and a rejected business is
        // the one case where that was an application decision rather than a revocation.
        var inactive = memberships.Where(membership => !membership.IsActive).ToList();
        var latestChange = inactive.Max(membership => membership.StatusChangedAtUtc);
        var latest = inactive
            .Where(membership => membership.StatusChangedAtUtc == latestChange)
            .ToList();

        if (latest.Any(membership => membership.BusinessStatus != BusinessStatus.Rejected))
        {
            return State(AccountState.MembershipRevoked);
        }

        // Every record at that instant is a rejection. Ordering by business id keeps the reported
        // reason deterministic in the - only theoretically reachable - case of two rejections
        // recorded at the same instant.
        return new AccountStateDecision(
            AccountState.ApplicationRejected,
            latest
                .OrderBy(membership => membership.BusinessId.Value)
                .First()
                .BusinessRejectionReason);
    }

    /// <summary>
    /// A state that carries no rejection reason, which is every state but one: a reason is only
    /// ever reported together with <see cref="AccountState.ApplicationRejected"/>.
    /// </summary>
    private static AccountStateDecision State(AccountState state) => new(state, RejectionReason: null);
}
