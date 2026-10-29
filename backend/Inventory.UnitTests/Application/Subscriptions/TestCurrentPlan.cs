using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests.Application.Subscriptions;

/// <summary>
/// The real <see cref="CurrentPlan"/> over the in-memory subscription fakes (issue #967), so a test
/// says "this business is on Starter" in one line instead of assembling a flag, a grandfathering
/// record, a grant list, a subscription and a clock.
///
/// <para>It builds the <em>real</em> use case and the real Domain rule deliberately: what a test
/// wants to fix is the business's plan, not the way a plan is decided from records -
/// <c>EffectiveEntitlementPolicyTests</c> is where that is tested row by row, and a hand-written
/// stub of <see cref="CurrentPlan"/> here would be a second, silently diverging answer to "is this
/// feature included".</para>
/// </summary>
public static class TestCurrentPlan
{
    private static readonly DateTime Now = new(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The shipped state: plan enforcement is off, so every feature is included and nothing about
    /// the business is read. This is what every existing test world is, which is why it is the
    /// default a construction site with nothing to say about plans uses.
    /// </summary>
    public static CurrentPlan Unrestricted() =>
        Build(FakeSubscriptionEnforcementFlag.Disabled, new FakeSubscriptionGrandfatheringStore(), []);

    /// <summary>
    /// Enforcement on, the grandfathering step completed, and the business holding an open-ended
    /// grant of <paramref name="plan"/> - the shape the grandfathering step itself writes.
    /// </summary>
    public static CurrentPlan On(SubscriptionPlan plan) =>
        Build(
            FakeSubscriptionEnforcementFlag.Enabled,
            FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
            [
                new ManualPlanGrantState(
                    plan,
                    ManualPlanGrantSource.Grandfathered,
                    new EntitlementPeriod(Cutoff, null)),
            ]);

    /// <summary>
    /// Enforcement on, the step completed and the business holding no record at all, which is how a
    /// business arrives on Free: the fail-closed answer for a paid feature.
    /// </summary>
    public static CurrentPlan Free() =>
        Build(
            FakeSubscriptionEnforcementFlag.Enabled,
            FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
            []);

    private static CurrentPlan Build(
        ISubscriptionEnforcementFlag flag,
        ISubscriptionGrandfatheringStore grandfathering,
        ManualPlanGrantState[] grants) =>
        new(new GetEffectiveEntitlement(
            flag,
            grandfathering,
            new FakeManualPlanGrantStore(grants),
            new FakeBillingSubscriptionStore(),
            new FakeClock(Now)));
}
