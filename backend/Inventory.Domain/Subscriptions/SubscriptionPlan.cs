namespace Inventory.Domain.Subscriptions;

/// <summary>
/// The plan a business is on (issue #966, under the agreed proposal of #965): the whole vocabulary,
/// ordered from least to most entitled.
///
/// <para>A plan is <strong>not</strong> a role and not a Nayax connection. A role says what a
/// member may do inside their business (<see cref="Tenancy.BusinessRole"/>); a plan says which
/// features that business has bought. The two are evaluated independently, and a plan never grants
/// a capability a role withholds or withholds one a role grants.</para>
///
/// <para>No plan is <c>0</c>, for the reason <see cref="Tenancy.BusinessStatus"/> declares none
/// either: <c>0</c> is what an unfilled integer column and a default-constructed enum both hold,
/// and the one answer that must never come from forgetting to decide is "this business is
/// entitled". The values are spaced so a plan can be declared between two of these without
/// renumbering one already persisted in a subscription or grant row.</para>
/// </summary>
public enum SubscriptionPlan
{
    /// <summary>
    /// A$0: the manual business. Dashboard, machines, inventory, purchasing, suppliers, expenses
    /// and manual pick-list entry, with 1 GB of purchase/expense attachments. No supplier price
    /// comparisons and no reports.
    /// </summary>
    Free = 10,

    /// <summary>
    /// Free plus 20 GB of attachments, the daily cheapest-supplier comparisons, and the two reports
    /// that need no provider: Bookkeeping and GST, with their exports.
    /// </summary>
    Starter = 20,

    /// <summary>
    /// Starter plus the Nayax connection and everything that depends on it, with five connected
    /// machines included and the rest billed per machine. Being above the allowance is billed,
    /// never blocked.
    /// </summary>
    Pro = 30,
}

/// <summary>
/// The declared plan vocabulary (issue #966), and the one comparison between two plans.
///
/// <para><strong>Only a declared plan is a plan.</strong> An enum is a compile-time constraint, not
/// a validation: a persisted integer column and a deserialized JSON number both carry an arbitrary
/// value, and a newer deployment may have written a plan this code does not know. Every reader
/// therefore asks <see cref="IsDeclared"/> first and treats anything else as a record it cannot
/// read - which <see cref="EffectiveEntitlementPolicy"/> resolves to
/// <see cref="SubscriptionPlan.Free"/> for paid features rather than to the plan it guessed at.</para>
/// </summary>
public static class SubscriptionPlans
{
    /// <summary>Every declared plan, least entitled first. The catalogue's own order.</summary>
    public static readonly IReadOnlyList<SubscriptionPlan> All =
    [
        SubscriptionPlan.Free,
        SubscriptionPlan.Starter,
        SubscriptionPlan.Pro,
    ];

    /// <summary>Whether <paramref name="plan"/> is one this code declares.</summary>
    public static bool IsDeclared(SubscriptionPlan plan) => All.Contains(plan);

    /// <summary>
    /// The more entitled of two plans - the "higher of" rule the effective-entitlement policy
    /// applies to an active manual grant and an active paid plan.
    ///
    /// <para>Comparison is by the declared order above rather than by the numeric value, so
    /// renumbering a plan cannot silently reorder the catalogue.</para>
    /// </summary>
    public static SubscriptionPlan Higher(SubscriptionPlan first, SubscriptionPlan second) =>
        RankOf(first) >= RankOf(second) ? first : second;

    /// <summary>
    /// Whether <paramref name="plan"/> is at least <paramref name="other"/>, by that same declared
    /// order.
    /// </summary>
    public static bool IsAtLeast(SubscriptionPlan plan, SubscriptionPlan other) =>
        RankOf(plan) >= RankOf(other);

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="plan"/> is not declared. A caller comparing a value it has not checked is
    /// the bug this refuses to paper over: an undeclared plan has no position in the catalogue, so
    /// there is no honest answer to "is it higher?".
    /// </exception>
    private static int RankOf(SubscriptionPlan plan)
    {
        var rank = All.ToList().IndexOf(plan);
        return rank >= 0
            ? rank
            : throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan,
                "Only a declared plan can be compared; check SubscriptionPlans.IsDeclared first.");
    }
}
