using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.PriceCatalogue;

/// <summary>
/// Which businesses the shared collector collects for (issue #977, child of #965): the one rule
/// behind both halves of that issue, stated on its own.
///
/// <para>The point of the rule being one function is that the surface a business reads prices
/// through and the collection that produces those prices cannot come to disagree: a business the
/// summary endpoint refuses must not have its products searched for at the suppliers, and a business
/// the endpoint serves must.</para>
/// </summary>
public class PriceCollectionEligibilityPolicyTests
{
    /// <summary>
    /// The feature is the same one the cheapest-supplier summary declares, and it is Starter -
    /// which is the issue's own first criterion ("shown only to Starter and Pro businesses").
    /// </summary>
    [Fact]
    public void The_gate_is_the_observed_supplier_prices_feature_and_it_is_starter()
    {
        Assert.Equal(PlanFeature.ObservedSupplierPrices, PriceCollectionEligibilityPolicy.Feature);
        Assert.Equal(
            SubscriptionPlan.Starter,
            PlanFeaturePolicy.RequiredPlan(PriceCollectionEligibilityPolicy.Feature));
    }

    /// <summary>
    /// Plan by plan: a Free business's tracked products are not collected, and Starter's and Pro's
    /// are. "A product tracked by both a Free and an entitled business is still collected" is the
    /// relational consequence of this and is asserted over the work list itself.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, false)]
    [InlineData(SubscriptionPlan.Starter, true)]
    [InlineData(SubscriptionPlan.Pro, true)]
    public void A_business_is_collected_for_only_once_its_plan_includes_the_prices(
        SubscriptionPlan plan,
        bool collected) =>
        Assert.Equal(
            collected,
            PriceCollectionEligibilityPolicy.CollectsFor(
                new EffectiveEntitlement(
                    Enforced: true, plan, EntitlementSource.ManualGrant, EndsAtUtc: null)));

    /// <summary>
    /// The issue's last criterion, from the Domain side: while nothing is enforced every business is
    /// collected for, exactly as it is today, on both of the two reasons an entitlement can be
    /// unenforced.
    /// </summary>
    [Theory]
    [InlineData(EntitlementSource.EnforcementDisabled)]
    [InlineData(EntitlementSource.GrandfatheringNotCompleted)]
    public void While_nothing_is_enforced_every_business_is_collected_for(EntitlementSource reason) =>
        Assert.True(
            PriceCollectionEligibilityPolicy.CollectsFor(
                EffectiveEntitlementPolicy.Unrestricted(reason)));

    /// <summary>
    /// And it fails closed in the same direction the feature policy does: an enforced entitlement
    /// with no readable plan is not collected for, rather than being given the nearest plan anybody
    /// could guess at.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData((SubscriptionPlan)99)]
    public void An_enforced_entitlement_with_no_readable_plan_is_not_collected_for(
        SubscriptionPlan? plan) =>
        Assert.False(
            PriceCollectionEligibilityPolicy.CollectsFor(
                new EffectiveEntitlement(
                    Enforced: true, plan, EntitlementSource.NoActiveRecord, EndsAtUtc: null)));
}
