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
    /// The decided table: the two provider-free reports and the observed supplier prices (issue
    /// #977) are Starter, and everything that depends on the sales provider - including the uploaded
    /// sales file, which reads no Nayax API at all - is Pro.
    /// </summary>
    [Theory]
    [InlineData(PlanFeature.SalesProviderIntegration, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.BookkeepingAndGstReports, SubscriptionPlan.Starter)]
    [InlineData(PlanFeature.ProviderSalesReports, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.SiteCommissions, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.SalesFileImport, SubscriptionPlan.Pro)]
    [InlineData(PlanFeature.ObservedSupplierPrices, SubscriptionPlan.Starter)]
    [InlineData(PlanFeature.ReceiptScanning, SubscriptionPlan.Pro)]
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
    public void The_declared_features_are_exactly_the_seven()
    {
        Assert.Equal(
            [
                PlanFeature.SalesProviderIntegration,
                PlanFeature.BookkeepingAndGstReports,
                PlanFeature.ProviderSalesReports,
                PlanFeature.SiteCommissions,
                PlanFeature.SalesFileImport,
                PlanFeature.ObservedSupplierPrices,
                PlanFeature.ReceiptScanning,
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
    /// any business": an unenforced entitlement includes every feature a business could already use,
    /// on both of the two reasons it can be unenforced.
    ///
    /// <para>The one exception is the feature that fails closed (issue #1163), asserted immediately
    /// below. It is excluded here by asking the same table rather than by naming it, so a second
    /// fail-closed feature cannot be added without both of these rules agreeing about it.</para>
    /// </summary>
    [Theory]
    [InlineData(EntitlementSource.EnforcementDisabled)]
    [InlineData(EntitlementSource.GrandfatheringNotCompleted)]
    public void An_unenforced_entitlement_includes_every_feature_that_does_not_fail_closed(
        EntitlementSource reason) =>
        Assert.All(
            PlanFeatures.All.Where(feature => !PlanFeaturePolicy.FailsClosedWhileUnenforced(feature)),
            feature => Assert.True(
                PlanFeaturePolicy.Allows(EffectiveEntitlementPolicy.Unrestricted(reason), feature)));

    /// <summary>
    /// The fail-closed rule (issue #1163): receipt scanning is refused while nothing is enforced, on
    /// both of the two reasons an entitlement can be unenforced, because every scan spends money at
    /// an external reader and "unrestricted" there would mean every business in the deployment.
    /// </summary>
    [Theory]
    [InlineData(EntitlementSource.EnforcementDisabled)]
    [InlineData(EntitlementSource.GrandfatheringNotCompleted)]
    public void The_fail_closed_feature_is_refused_while_nothing_is_enforced(EntitlementSource reason) =>
        Assert.False(
            PlanFeaturePolicy.Allows(
                EffectiveEntitlementPolicy.Unrestricted(reason), PlanFeature.ReceiptScanning));

    /// <summary>
    /// Exactly one feature fails closed, pinned by value: making a second one do so is a commercial
    /// decision about who pays for a feature, so it is a change somebody has to see in a diff.
    /// </summary>
    [Fact]
    public void Exactly_the_receipt_scan_feature_fails_closed()
    {
        Assert.Equal(
            [PlanFeature.ReceiptScanning],
            PlanFeatures.All.Where(PlanFeaturePolicy.FailsClosedWhileUnenforced));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanFeaturePolicy.FailsClosedWhileUnenforced((PlanFeature)99));
    }

    /// <summary>
    /// The plan × feature matrix, as the plan page advertises it: Free includes none of them,
    /// Starter adds the two provider-free reports and the observed supplier prices, and Pro includes
    /// everything.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, PlanFeature.SalesProviderIntegration, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.BookkeepingAndGstReports, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.ProviderSalesReports, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.SiteCommissions, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.SalesFileImport, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.ObservedSupplierPrices, false)]
    [InlineData(SubscriptionPlan.Free, PlanFeature.ReceiptScanning, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.SalesProviderIntegration, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.BookkeepingAndGstReports, true)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.ProviderSalesReports, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.SiteCommissions, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.SalesFileImport, false)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.ObservedSupplierPrices, true)]
    [InlineData(SubscriptionPlan.Starter, PlanFeature.ReceiptScanning, false)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.SalesProviderIntegration, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.BookkeepingAndGstReports, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.ProviderSalesReports, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.SiteCommissions, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.SalesFileImport, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.ObservedSupplierPrices, true)]
    [InlineData(SubscriptionPlan.Pro, PlanFeature.ReceiptScanning, true)]
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
