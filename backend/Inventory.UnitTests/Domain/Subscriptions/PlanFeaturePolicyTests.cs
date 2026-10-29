using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.Subscriptions;

/// <summary>
/// The plan requirement of every feature (issue #967), and whether one effective entitlement
/// satisfies it, stated literally.
///
/// <para>The table is pinned by value rather than derived, for the reason
/// <c>PlanCatalogueTests</c> pins the prices: which plan a feature needs is a commercial decision
/// recorded on issue #965 and its review of 29 October 2026, so a change to one of these rows is a
/// change somebody has to see in a diff.</para>
/// </summary>
public class PlanFeaturePolicyTests
{
    /// <summary>
    /// The decided table: the two provider-free reports are Starter, and everything that depends on
    /// the sales provider - including the uploaded sales file, which reads no Nayax API at all - is
    /// Pro.
    /// </summary>
    [Theory]
    [InlineData(PlanFeature.SalesProviderIntegration, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.BookkeepingAndGstReports, SubscriptionPlan.Starter)]
    [InlineData(PlanFeature.ProviderSalesReports, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.SiteCommissions, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.SalesFileImport, SubscriptionPlan.Pro)]
    public void Every_feature_requires_the_decided_plan(PlanFeature feature, SubscriptionPlan expected) =>
        Assert.Equal(expected, PlanFeaturePolicy.RequiredPlan(feature));

    /// <summary>
    /// No feature is free. It is the one row that is asserted as a property rather than a value:
    /// Free's own features are simply not plan features at all, so a <c>Free</c> requirement here
    /// would be a feature nothing could ever withhold - and the enforcement points would be dead
    /// code nobody noticed.
    /// </summary>
    [Fact]
    public void No_declared_feature_is_included_in_free() =>
        Assert.All(
            PlanFeatures.All,
            feature => Assert.NotEqual(SubscriptionPlan.Free, PlanFeaturePolicy.RequiredPlan(feature)));

    /// <summary>The declared vocabulary, so a feature added later is added here too.</summary>
    [Fact]
    public void The_declared_features_are_exactly_the_five()
    {
        Assert.Equal(
            [
                PlanFeature.SalesProviderIntegration,
                PlanFeature.BookkeepingAndGstReports,
                PlanFeature.ProviderSalesReports,
                PlanFeature.SiteCommissions,
                PlanFeature.SalesFileImport,
            ],
            PlanFeatures.All);
        Assert.Equal(PlanFeatures.All.Count, Enum.GetValues<PlanFeature>().Length);
        Assert.All(PlanFeatures.All, feature => Assert.True(PlanFeatures.IsDeclared(feature)));
    }

    /// <summary>
    /// An enum is a compile-time constraint, not a validation: a value cast from an integer can be
    /// anything. A feature nobody declared has no required plan to answer with.
    /// </summary>
    [Fact]
    public void An_undeclared_feature_has_no_required_plan_and_no_name()
    {
        Assert.False(PlanFeatures.IsDeclared((PlanFeature)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanFeaturePolicy.RequiredPlan((PlanFeature)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanFeatures.Name((PlanFeature)99));
    }

    /// <summary>
    /// The shipped state, and the criterion "until enforcement is switched on, nothing changes for
    /// any business": an unenforced entitlement includes every feature, on both of the two reasons
    /// it can be unenforced.
    /// </summary>
    [Theory]
    [InlineData(EntitlementSource.EnforcementDisabled)]
    [InlineData(EntitlementSource.GrandfatheringNotCompleted)]
    public void An_unenforced_entitlement_includes_every_feature(EntitlementSource reason) =>
        Assert.All(
            PlanFeatures.All,
            feature => Assert.True(
                PlanFeaturePolicy.Allows(EffectiveEntitlementPolicy.Unrestricted(reason), feature)));

    /// <summary>
    /// The plan × feature matrix, as the plan page advertises it: Free includes none of them,
    /// Starter adds only the two provider-free reports, and Pro includes everything.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, PlanFeature.SalesProviderIntegration, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.BookkeepingAndGstReports, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.ProviderSalesReports, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.SiteCommissions, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.SalesFileImport, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.SalesProviderIntegration, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.BookkeepingAndGstReports, true)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.ProviderSalesReports, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.SiteCommissions, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.SalesFileImport, false)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.SalesProviderIntegration, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.BookkeepingAndGstReports, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.ProviderSalesReports, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.SiteCommissions, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.SalesFileImport, true)]
    public void Each_plan_includes_exactly_the_features_the_catalogue_advertises(
        SubscriptionPlan plan,
        PlanFeature feature,
        bool included) =>
        Assert.Equal(included, PlanFeaturePolicy.Allows(Enforcing(plan), feature));

    /// <summary>
    /// The second line of defence. <c>EffectiveEntitlementPolicy</c> already resolves an unreadable
    /// record to Free, so an enforced entitlement should never arrive here carrying no plan or a
    /// plan this deployment does not declare - and if one does, it includes nothing rather than the
    /// nearest plan anybody could guess at.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData((SubscriptionPlan)99)]
    public void An_enforced_entitlement_with_no_readable_plan_includes_nothing(SubscriptionPlan? plan) =>
        Assert.All(
            PlanFeatures.All,
            feature => Assert.False(
                PlanFeaturePolicy.Allows(
                    new EffectiveEntitlement(
                        Enforced: true, plan, EntitlementSource.NoActiveRecord, EndsAtUtc: null),
                    feature)));

    /// <summary>
    /// An undeclared feature is refused before the unenforced shortcut, so a programming error is
    /// not silently allowed by the shipped state - which is where it would be hardest to notice.
    /// </summary>
    [Fact]
    public void An_undeclared_feature_is_refused_even_while_nothing_is_enforced() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanFeaturePolicy.Allows(
            EffectiveEntitlementPolicy.Unrestricted(EntitlementSource.EnforcementDisabled),
            (PlanFeature)99));

    [Fact]
    public void A_missing_entitlement_is_refused_rather_than_treated_as_unrestricted() =>
        Assert.Throws<ArgumentNullException>(() => PlanFeaturePolicy.Allows(
            null!, PlanFeature.SalesProviderIntegration));

    private static EffectiveEntitlement Enforcing(SubscriptionPlan plan) =>
        new(Enforced: true, plan, EntitlementSource.ManualGrant, EndsAtUtc: null);
}
