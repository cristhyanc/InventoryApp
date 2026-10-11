namespace Inventory.Domain.Tenancy;

/// <summary>
/// The one rule that turns an identity's membership records into its <see cref="AccountState"/>
/// (issue #523), with the precedence agreed in #328: first match wins.
///
/// <list type="number">
///   <item><see cref="AccountState.MembershipAmbiguous"/>: more than one active membership.</item>
///   <item><see cref="AccountState.Member"/>: one active membership in an active business.</item>
///   <item><see cref="AccountState.ApplicationPending"/>: one active membership in a pending
///   business (issue #507's business status).</item>
///   <item><see cref="AccountState.BusinessDeactivated"/>: one active membership in a deactivated
///   business.</item>
///   <item>No active membership: the most recent inactive record decides - an inactive membership
///   in a rejected business gives <see cref="AccountState.ApplicationRejected"/> (issue #507), any
///   other gives <see cref="AccountState.MembershipRevoked"/>.</item>
///   <item><see cref="AccountState.NoMembership"/>: no rows.</item>
/// </list>
///
/// <para>The ordering is the whole rule. A person's history may hold any number of revoked rows,
/// and the one thing that must never happen is an old revocation describing an account that works
/// today: the active membership is examined before the inactive ones, not merged with them.</para>
///
/// <para><strong>Steps 3 and 5's rejected case are not reachable yet.</strong> Both read a
/// business status - <c>Pending</c>, <c>Rejected</c> - that no business carries until issue #507
/// adds it, so today step 3 cannot match and every inactive record falls to
/// <see cref="AccountState.MembershipRevoked"/>. That also makes "the most recent inactive record"
/// unobservable rather than unimplemented: with one possible answer for an inactive record, which
/// record is most recent cannot change the state. The column it will be ordered by,
/// <c>BusinessMembership.StatusChangedAtUtc</c>, is already stored and filled (issue #522), so
/// #507 adds the status to <see cref="ActorBusinessMembership"/>, the ordering and the two states
/// together.</para>
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
    public static AccountState Determine(IReadOnlyCollection<ActorBusinessMembership>? memberships)
    {
        if (memberships is null || memberships.Count == 0)
        {
            return AccountState.NoMembership;
        }

        var active = memberships.Where(membership => membership.IsActive).ToList();

        if (active.Count > 1)
        {
            return AccountState.MembershipAmbiguous;
        }

        if (active.Count == 1)
        {
            // Step 3, the pending business, belongs between these two once #507 adds the status.
            return active[0].BusinessIsActive
                ? AccountState.Member
                : AccountState.BusinessDeactivated;
        }

        // Every record is inactive. The account held a membership and no longer does, whatever
        // state the business it belonged to is in; the rejected-application case #507 adds is the
        // one exception, and it reads a business status that does not exist yet.
        return AccountState.MembershipRevoked;
    }
}
