namespace Inventory.Domain.Subscriptions;

/// <summary>
/// The features a business's plan decides (issue #967, under the agreed proposal of #965): the whole
/// vocabulary, one member per thing a plan can withhold.
///
/// <para><strong>A feature is not an endpoint.</strong> Several endpoints, exports and pages are
/// behind one feature - every ordinary Nayax call in the application is
/// <see cref="SalesProviderIntegration"/> - and that is the point: the plan a feature needs is
/// decided once, in <see cref="PlanFeaturePolicy"/>, and an endpoint declares the feature rather than
/// restating a plan. A report and its CSV/XLSX export therefore cannot come to need different plans,
/// for the same reason a presentation never restates a financial formula.</para>
///
/// <para><strong>A feature is never a capability.</strong> A capability says what a member may do
/// inside their business (<c>Inventory.Application.Access.Capability</c>, through
/// <c>RoleCapabilities</c>); a feature says what the business has bought. The two are evaluated
/// independently and in that order, and a plan never grants a role more access than it holds - which
/// is why no feature here is named after a role and no capability is named here.</para>
///
/// <para>Numbered from 10 and spaced, for the reason <see cref="SubscriptionPlan"/> is: <c>0</c> is
/// what a default-constructed enum holds, and the one answer that must never come from forgetting to
/// decide is "this is free for everybody".</para>
/// </summary>
public enum PlanFeature
{
    /// <summary>
    /// The sales-provider connection and everything that reads or writes through it: every ordinary
    /// Nayax call (the credential gate behind
    /// <c>Inventory.Application.Nayax.INayaxRequestCredentialProvider</c>) and the Owner's credential
    /// actions beside it, which call Nayax without that gate - test, save and re-test.
    ///
    /// <para>Reading the stored connection <em>status</em> is deliberately not this feature: it makes
    /// no Nayax call, it is what tells a business its credentials are still there after a downgrade,
    /// and the banners that show it are on every page.</para>
    /// </summary>
    SalesProviderIntegration = 10,

    /// <summary>
    /// The two reports that need no sales provider - Bookkeeping and the GST accounting aid - with
    /// their CSV/XLSX exports. They are calculated from the business's own stored records, which is
    /// why they are the two reports a plan without a provider connection still includes.
    /// </summary>
    BookkeepingAndGstReports = 20,

    /// <summary>
    /// Every other report and its export: daily, transactions, reconciliation, machine
    /// profitability, product profitability and the reporting dashboard. Each of them is a reading of
    /// the provider's sales, so the plan that includes the provider is the plan that includes them.
    /// </summary>
    ProviderSalesReports = 30,

    /// <summary>
    /// Site commissions - the report, the agreements and the payments. A site is known only from the
    /// live fleet read's <c>CustomerID</c> (issue #731), so commission is a provider feature even
    /// where the agreement itself is a stored record.
    /// </summary>
    SiteCommissions = 40,

    /// <summary>
    /// The uploaded Nayax sales/transaction export under Admin → Imports
    /// (<c>POST /api/imports/nayax-sales</c>), decided on #965's review of 29 October 2026. It reads
    /// a file rather than the Nayax API, so the credential gate never sees it and it needs a plan
    /// requirement of its own.
    /// </summary>
    SalesFileImport = 50,
}

/// <summary>
/// The declared plan-feature vocabulary (issue #967), and the published name of each member.
///
/// <para><strong>Only a declared feature is a feature.</strong> An enum is a compile-time constraint,
/// not a validation: a value cast from an integer can be anything, so every reader asks
/// <see cref="IsDeclared"/> - or relies on <see cref="PlanFeaturePolicy.RequiredPlan"/>, which
/// refuses one rather than answering with a plan nobody decided.</para>
/// </summary>
public static class PlanFeatures
{
    /// <summary>Every declared feature, in the enum's own order.</summary>
    public static readonly IReadOnlyList<PlanFeature> All =
    [
        PlanFeature.SalesProviderIntegration,
        PlanFeature.BookkeepingAndGstReports,
        PlanFeature.ProviderSalesReports,
        PlanFeature.SiteCommissions,
        PlanFeature.SalesFileImport,
    ];

    /// <summary>Whether <paramref name="feature"/> is one this code declares.</summary>
    public static bool IsDeclared(PlanFeature feature) => All.Contains(feature);

    /// <summary>
    /// The feature's published name, written out rather than taken from the enum member so renaming
    /// a C# member cannot silently change what a log line or a test inventory says.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="feature"/> is not declared. An undeclared value has no name to publish.
    /// </exception>
    public static string Name(PlanFeature feature) => feature switch
    {
        PlanFeature.SalesProviderIntegration => "SalesProviderIntegration",
        PlanFeature.BookkeepingAndGstReports => "BookkeepingAndGstReports",
        PlanFeature.ProviderSalesReports => "ProviderSalesReports",
        PlanFeature.SiteCommissions => "SiteCommissions",
        PlanFeature.SalesFileImport => "SalesFileImport",
        _ => throw new ArgumentOutOfRangeException(
            nameof(feature), feature, "Only a declared plan feature has a published name."),
    };
}

/// <summary>
/// Which plan each feature needs, and whether one effective entitlement satisfies it (issue #967,
/// from the decisions recorded on #965 and its review of 29 October 2026).
///
/// <para><strong>It is the single table, and it is a pure function.</strong> No clock, no database,
/// no configuration, no caller identity and no endpoint: the whole rule is the switch below plus
/// <see cref="SubscriptionPlans.IsAtLeast"/>, so what an endpoint enforces, what the provider
/// credential gate refuses and what the plan page will eventually advertise cannot disagree. The
/// enforcement points supply it with facts; none of them restates a plan.</para>
///
/// <para><strong>Not enforced is not a plan.</strong> While the rollout setting is off, or while the
/// grandfathering step has not completed, <see cref="EffectiveEntitlement.Enforced"/> is
/// <see langword="false"/> and <see cref="Allows"/> is <see langword="true"/> for every feature -
/// which is the shipped state, and is what makes "until enforcement is switched on, nothing changes
/// for any business" a property of this one rule rather than of each call site.</para>
///
/// <para><strong>Otherwise it fails closed.</strong> An entitlement that is enforced and carries no
/// plan, or carries one this deployment does not declare, allows nothing that is not free: there is
/// no plan to compare against, and guessing the nearest one would hand out a feature nobody bought.
/// <see cref="EffectiveEntitlementPolicy"/> already resolves an unreadable record to
/// <see cref="SubscriptionPlan.Free"/>, so this is the second line rather than the first.</para>
/// </summary>
public static class PlanFeaturePolicy
{
    /// <summary>
    /// The least plan that includes <paramref name="feature"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="feature"/> is not declared (see <see cref="PlanFeatures.IsDeclared"/>). There
    /// is no requirement to invent for a feature this code does not know, and answering with one
    /// would be answering with a plan nobody decided.
    /// </exception>
    public static SubscriptionPlan RequiredPlan(PlanFeature feature) => feature switch
    {
        PlanFeature.SalesProviderIntegration => SubscriptionPlan.Pro,
        PlanFeature.BookkeepingAndGstReports => SubscriptionPlan.Starter,
        PlanFeature.ProviderSalesReports => SubscriptionPlan.Pro,
        PlanFeature.SiteCommissions => SubscriptionPlan.Pro,
        PlanFeature.SalesFileImport => SubscriptionPlan.Pro,
        _ => throw new ArgumentOutOfRangeException(
            nameof(feature), feature, "Only a declared plan feature has a required plan."),
    };

    /// <summary>
    /// Whether <paramref name="entitlement"/> includes <paramref name="feature"/>.
    /// </summary>
    /// <param name="entitlement">
    /// What the business may currently use, as <see cref="EffectiveEntitlementPolicy.Decide"/>
    /// decided it.
    /// </param>
    /// <param name="feature">The feature being asked about.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="feature"/> is not declared.
    /// </exception>
    public static bool Allows(EffectiveEntitlement entitlement, PlanFeature feature)
    {
        ArgumentNullException.ThrowIfNull(entitlement);

        // Asked first, so an undeclared feature is refused as the programming error it is rather
        // than silently allowed by the unenforced branch below.
        var required = RequiredPlan(feature);

        if (!entitlement.Enforced)
        {
            return true;
        }

        return entitlement.Plan is { } plan
            && SubscriptionPlans.IsDeclared(plan)
            && SubscriptionPlans.IsAtLeast(plan, required);
    }
}
