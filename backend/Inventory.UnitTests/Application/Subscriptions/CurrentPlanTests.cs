using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using InventoryApi.Tests.Application.ConnectedMachines;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Subscriptions;

/// <summary>
/// The per-request plan read every enforcement point goes through (issue #967).
///
/// <para>What it decides is <c>PlanFeaturePolicy</c>'s and is tested row by row there. What only
/// this file can show is the property the enforcement points depend on: the entitlement is read
/// <strong>once per request</strong> and shared, which is what keeps a bounded Nayax fan-out from
/// turning one plan question into one database read per machine.</para>
/// </summary>
public class CurrentPlanTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Many questions, one read. The grants and the subscription are each read once however often
    /// the request asks - which is the fan-out case, stated as the property rather than as a
    /// scenario.
    /// </summary>
    [Fact]
    public async Task The_entitlement_is_read_once_however_often_the_request_asks()
    {
        var grandfathering = FakeSubscriptionGrandfatheringStore.Completed(Cutoff);
        var grants = new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Pro));
        var subscriptions = new FakeBillingSubscriptionStore();
        var plan = Build(FakeSubscriptionEnforcementFlag.Enabled, grandfathering, grants, subscriptions);

        for (var question = 0; question < 5; question++)
        {
            Assert.True(await plan.AllowsAsync(PlanFeature.SalesProviderIntegration, CancellationToken.None));
        }

        Assert.Equal(1, grandfathering.Reads);
        Assert.Equal(1, grants.Reads);
        Assert.Equal(1, subscriptions.Reads);
    }

    /// <summary>
    /// And once even when the questions are concurrent, which is exactly how the fan-outs ask: the
    /// request has one persistence scope, and a second simultaneous read on it is the failure this
    /// memoisation exists to prevent.
    /// </summary>
    [Fact]
    public async Task Concurrent_questions_share_the_one_read()
    {
        var grants = new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Pro));
        var plan = Build(
            FakeSubscriptionEnforcementFlag.Enabled,
            FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
            grants,
            new FakeBillingSubscriptionStore());

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            plan.AllowsAsync(PlanFeature.SalesProviderIntegration, CancellationToken.None)));

        Assert.All(answers, Assert.True);
        Assert.Equal(1, grants.Reads);
    }

    /// <summary>
    /// The shipped state: with the rollout setting off every feature is included and nothing about
    /// the business is read at all, so a deployment that has switched nothing on pays nothing for
    /// this check.
    /// </summary>
    [Fact]
    public async Task With_enforcement_off_every_feature_is_included_and_nothing_is_read()
    {
        var grandfathering = new FakeSubscriptionGrandfatheringStore();
        var grants = new FakeManualPlanGrantStore();
        var plan = Build(
            FakeSubscriptionEnforcementFlag.Disabled,
            grandfathering,
            grants,
            new FakeBillingSubscriptionStore());

        foreach (var feature in PlanFeatures.All)
        {
            Assert.True(await plan.AllowsAsync(feature, CancellationToken.None));
        }

        Assert.Equal(0, grandfathering.Reads);
        Assert.Equal(0, grants.Reads);
    }

    /// <summary>
    /// The enforced answer, and that it is the business's own plan rather than a fixed one: Starter
    /// includes the two provider-free reports and withholds everything behind the provider.
    /// </summary>
    [Fact]
    public async Task An_enforced_business_is_answered_from_its_own_plan()
    {
        var plan = Build(
            FakeSubscriptionEnforcementFlag.Enabled,
            FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
            new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Starter)),
            new FakeBillingSubscriptionStore());

        Assert.True(await plan.AllowsAsync(PlanFeature.BookkeepingAndGstReports, CancellationToken.None));
        Assert.False(await plan.AllowsAsync(PlanFeature.ProviderSalesReports, CancellationToken.None));
        Assert.False(await plan.AllowsAsync(PlanFeature.SalesProviderIntegration, CancellationToken.None));
    }

    /// <summary>
    /// The entitlement itself is reachable, for a caller that needs to report it rather than only
    /// branch on it, and it is the same object every asker gets.
    /// </summary>
    [Fact]
    public async Task The_entitlement_itself_is_the_same_object_for_every_asker()
    {
        var plan = Build(
            FakeSubscriptionEnforcementFlag.Enabled,
            FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
            new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Pro)),
            new FakeBillingSubscriptionStore());

        var first = await plan.CurrentAsync(CancellationToken.None);
        var second = await plan.CurrentAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(SubscriptionPlan.Pro, first.Plan);
    }

    private static ManualPlanGrantState Grant(SubscriptionPlan plan) =>
        new(plan, ManualPlanGrantSource.Grandfathered, new EntitlementPeriod(Cutoff, null));

    private static CurrentPlan Build(
        ISubscriptionEnforcementFlag flag,
        ISubscriptionGrandfatheringStore grandfathering,
        IManualPlanGrantStore grants,
        IBillingSubscriptionStore subscriptions) =>
        new(new GetEffectiveEntitlement(
            flag,
            grandfathering,
            grants,
            subscriptions,
            // The connected machines of issue #978 travel with the plan; a plan feature question
            // never reads them, so the business here has no Nayax connection and no recorded count.
            new FakeNayaxConnectionStore(),
            new FakeConnectedMachineSnapshotStore(),
            new FakeClock(Now)));
}
