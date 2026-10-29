using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Subscriptions;

/// <summary>
/// The use case behind the entitlement <c>GET /api/me/access</c> publishes (issue #966), without
/// ASP.NET Core or EF Core.
///
/// <para>The decisions themselves belong to <c>EffectiveEntitlementPolicy</c> and are tested row by
/// row there. What this file has to show is the use case's own three properties: that it supplies the
/// policy with the right facts, that it reads <em>nothing</em> about a business while nothing is
/// enforced, and that it takes no identifier at all - the business is whichever one the request
/// resolved, so there is no parameter a caller could use to ask about somebody else's plan.</para>
/// </summary>
public class GetEffectiveEntitlementTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// With the rollout setting off the answer is unrestricted, and nothing about the business is
    /// read at all: a deployment that has switched nothing on makes no extra query per request, and
    /// no plan is published for a business nothing is restricting.
    /// </summary>
    [Fact]
    public async Task With_enforcement_off_nothing_is_read_and_the_answer_is_unrestricted()
    {
        var grandfathering = new FakeSubscriptionGrandfatheringStore();
        var grants = new FakeManualPlanGrantStore();
        var subscriptions = new FakeBillingSubscriptionStore();

        var entitlement = await UseCase(
                FakeSubscriptionEnforcementFlag.Disabled,
                grandfathering,
                grants,
                subscriptions)
            .Handle(CancellationToken.None);

        Assert.False(entitlement.Enforced);
        Assert.Null(entitlement.Plan);
        Assert.Equal(EntitlementSource.EnforcementDisabled, entitlement.Source);
        Assert.Equal(0, grandfathering.Reads);
        Assert.Equal(0, grants.Reads);
        Assert.Equal(0, subscriptions.Reads);
    }

    /// <summary>
    /// With the setting on but the step unfinished the answer is unrestricted too, and the
    /// business's own records are still not read: there is nothing to read them for.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_grandfathering_unfinished_the_business_records_are_not_read(bool started)
    {
        var grandfathering = started
            ? FakeSubscriptionGrandfatheringStore.Started(Cutoff)
            : new FakeSubscriptionGrandfatheringStore();
        var grants = new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Pro));
        var subscriptions = new FakeBillingSubscriptionStore();

        var entitlement = await UseCase(
                FakeSubscriptionEnforcementFlag.Enabled,
                grandfathering,
                grants,
                subscriptions)
            .Handle(CancellationToken.None);

        Assert.False(entitlement.Enforced);
        Assert.Null(entitlement.Plan);
        Assert.Equal(EntitlementSource.GrandfatheringNotCompleted, entitlement.Source);
        Assert.Equal(1, grandfathering.Reads);
        Assert.Equal(0, grants.Reads);
        Assert.Equal(0, subscriptions.Reads);
    }

    [Fact]
    public async Task Once_the_step_has_completed_both_records_are_read_and_the_higher_plan_is_reported()
    {
        var grants = new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Pro));
        var subscriptions = new FakeBillingSubscriptionStore(
            new BillingSubscriptionState(
                SubscriptionPlan.Starter,
                BillingSubscriptionStatus.Active,
                new EntitlementPeriod(Cutoff, null)));

        var entitlement = await UseCase(
                FakeSubscriptionEnforcementFlag.Enabled,
                FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
                grants,
                subscriptions)
            .Handle(CancellationToken.None);

        Assert.True(entitlement.Enforced);
        Assert.Equal(SubscriptionPlan.Pro, entitlement.Plan);
        Assert.Equal(EntitlementSource.ManualGrant, entitlement.Source);
        Assert.Equal(1, grants.Reads);
        Assert.Equal(1, subscriptions.Reads);
    }

    [Fact]
    public async Task A_business_with_no_record_is_reported_on_free_once_enforcement_applies()
    {
        var entitlement = await UseCase(
                FakeSubscriptionEnforcementFlag.Enabled,
                FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
                new FakeManualPlanGrantStore(),
                new FakeBillingSubscriptionStore())
            .Handle(CancellationToken.None);

        Assert.True(entitlement.Enforced);
        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
        Assert.Equal(EntitlementSource.NoActiveRecord, entitlement.Source);
    }

    /// <summary>
    /// The instant comes from the application clock, so a grant that has just ended is over on the
    /// next request rather than when something expires a cache.
    /// </summary>
    [Fact]
    public async Task The_instant_is_the_application_clocks()
    {
        var grants = new FakeManualPlanGrantStore(
            new ManualPlanGrantState(
                SubscriptionPlan.Pro,
                ManualPlanGrantSource.Complimentary,
                new EntitlementPeriod(Cutoff, Now)));

        var entitlement = await UseCase(
                FakeSubscriptionEnforcementFlag.Enabled,
                FakeSubscriptionGrandfatheringStore.Completed(Cutoff),
                grants,
                new FakeBillingSubscriptionStore())
            .Handle(CancellationToken.None);

        // The grant's end instant is exclusive and the clock is exactly at it, so it is over.
        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
    }

    private static GetEffectiveEntitlement UseCase(
        ISubscriptionEnforcementFlag flag,
        ISubscriptionGrandfatheringStore grandfathering,
        IManualPlanGrantStore grants,
        IBillingSubscriptionStore subscriptions) =>
        new(flag, grandfathering, grants, subscriptions, new FakeClock(Now));

    private static ManualPlanGrantState Grant(SubscriptionPlan plan) =>
        new(plan, ManualPlanGrantSource.Grandfathered, new EntitlementPeriod(Cutoff, null));
}
