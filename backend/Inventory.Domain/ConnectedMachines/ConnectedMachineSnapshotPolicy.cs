using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;

namespace Inventory.Domain.ConnectedMachines;

/// <summary>
/// Which businesses are counted, which connection may be read, and which snapshot is a day's count
/// (issue #978).
///
/// <para>It is a pure function of the facts handed in - the business's own plan records, its stored
/// connection status, the snapshots it holds and the instant being asked about - with no clock, no
/// database and no caller identity of its own.</para>
/// </summary>
public static class ConnectedMachineSnapshotPolicy
{
    /// <summary>
    /// The lowest plan whose connected machines are counted: <see cref="SubscriptionPlan.Pro"/>, the
    /// one plan that has a Nayax connection and an included machine allowance at all
    /// (<c>PlanCatalogue</c>).
    /// </summary>
    public const SubscriptionPlan CountedFrom = SubscriptionPlan.Pro;

    /// <summary>
    /// The plan the business's own records support, <strong>with the rollout gates deliberately set
    /// aside</strong>.
    ///
    /// <para>This is not a second entitlement rule: it is
    /// <see cref="EffectiveEntitlementPolicy.Decide"/> itself, asked what the records say, so "the
    /// higher of the active grant and the active paid plan", the half-open periods and every way a
    /// record fails to entitle anything stay in exactly one place. What it sets aside is the
    /// <em>rollout</em> switch and the grandfathering marker, and it does so because billing evidence
    /// has to accrue <em>before</em> enforcement is switched on: #978's own criterion is that until
    /// enforcement starts "snapshots may be recorded but nothing is shown or billed". A count taken
    /// while nothing was enforced is still the count the account held that day.</para>
    ///
    /// <para>It grants nothing and withholds nothing. No feature is offered or refused anywhere on
    /// the strength of this answer - it decides only whether a business's fleet is counted.</para>
    /// </summary>
    public static SubscriptionPlan PlanOnRecord(
        IReadOnlyList<ManualPlanGrantState> manualGrants,
        BillingSubscriptionState? billingSubscription,
        DateTime nowUtc) =>
        EffectiveEntitlementPolicy.Decide(
                enforcementEnabled: true,
                grandfatheringCompleted: true,
                manualGrants,
                billingSubscription,
                nowUtc)
            .Plan
        ?? SubscriptionPlan.Free;

    /// <summary>
    /// Whether a business on <paramref name="plan"/> has its connected machines counted at all.
    ///
    /// <para>Asked as "at least Pro" rather than "is Pro" so a plan declared above Pro later is
    /// counted on the day it is declared rather than silently dropping out of billing evidence.</para>
    /// </summary>
    public static bool RecordsSnapshots(SubscriptionPlan plan) =>
        SubscriptionPlans.IsDeclared(plan) && SubscriptionPlans.IsAtLeast(plan, CountedFrom);

    /// <summary>
    /// Whether the fleet may be read for a connection in <paramref name="status"/>.
    ///
    /// <para>It is <see cref="NayaxConnectionStatusGate.AllowsOrdinaryOperations"/> and nothing else:
    /// counting machines is an ordinary Nayax read, so it runs exactly where every other one does.
    /// A disconnected connection and one that <see cref="NayaxConnectionStatus.NeedsAttention"/>
    /// make <strong>no call at all</strong> - there is no credential a snapshot is worth spending,
    /// and the count is reported as unavailable instead (<see cref="ConnectedMachineCountPolicy"/>).</para>
    /// </summary>
    public static bool MayReadFleet(NayaxConnectionStatus status) =>
        NayaxConnectionStatusGate.AllowsOrdinaryOperations(status);

    /// <summary>
    /// The later of two snapshots as evidence: the later UTC day wins, and on one day the later
    /// account series wins.
    ///
    /// <para>The series part is #978's rule for a day the Nayax account changed on: that day can hold
    /// one snapshot per series, the old series' row is left exactly as it was, and <strong>the day's
    /// count is the later series' snapshot</strong> - the account the business actually ended the day
    /// on. It is written once, here, because the entitlement read and the billing of #969 must not be
    /// able to answer that question differently.</para>
    /// </summary>
    public static ConnectedMachineSnapshot? Latest(IReadOnlyList<ConnectedMachineSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        ConnectedMachineSnapshot? latest = null;

        foreach (var snapshot in snapshots)
        {
            if (latest is null
                || snapshot.DateUtc > latest.DateUtc
                || (snapshot.DateUtc == latest.DateUtc && snapshot.AccountSeries > latest.AccountSeries))
            {
                latest = snapshot;
            }
        }

        return latest;
    }
}
