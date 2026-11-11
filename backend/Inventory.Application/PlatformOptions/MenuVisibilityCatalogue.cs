namespace Inventory.Application.PlatformOptions;

/// <summary>
/// The key of every switchable menu destination, named once (issue #1148). Nothing in the
/// application spells one of these strings a second time: a key is stored in a row, published to
/// every caller and submitted back by the Super Admin surface, so a second spelling of one is a
/// second chance to get it wrong.
///
/// <para>A key names a <strong>destination</strong>, not a route. Which menu entries use a key is
/// the frontend's decision (issue #1151), so several entries may share one - the Reimbursement
/// Report of #1152 shares <see cref="Imports"/>, and no new key is added for it.</para>
///
/// <para>Each key is namespaced by the menu group its destination belongs to, which is what let the
/// Reports group of issue #1153 arrive beside the Admin keys with no storage change at all: the
/// stored row is keyed by this string and knows nothing about groups.</para>
/// </summary>
public static class MenuVisibilityKeys
{
    /// <summary>The Bookkeeping report.</summary>
    public const string ReportsBookkeeping = "reports.bookkeeping";

    /// <summary>The Daily report.</summary>
    public const string ReportsDaily = "reports.daily";

    /// <summary>The Transactions report.</summary>
    public const string ReportsTransactions = "reports.transactions";

    /// <summary>The Reconciliation report.</summary>
    public const string ReportsReconciliation = "reports.reconciliation";

    /// <summary>The Machines report.</summary>
    public const string ReportsMachines = "reports.machines";

    /// <summary>The Products report.</summary>
    public const string ReportsProducts = "reports.products";

    /// <summary>The Site Commissions report.</summary>
    public const string ReportsSiteCommissions = "reports.site-commissions";

    /// <summary>The GST report.</summary>
    public const string ReportsGst = "reports.gst";

    /// <summary>Site Commission Agreements.</summary>
    public const string SiteCommissionAgreements = "admin.site-commission-agreements";

    /// <summary>
    /// The Nayax sales and settlement imports - and, since issue #1152, the Reimbursement Report
    /// that is reached from them.
    /// </summary>
    public const string Imports = "admin.imports";

    /// <summary>Historical Cost Recovery.</summary>
    public const string HistoricalCostRecovery = "admin.historical-cost-recovery";

    /// <summary>AVCO Transition.</summary>
    public const string AvcoTransition = "admin.avco-transition";

    /// <summary>Costing Repair.</summary>
    public const string CostingRepair = "admin.costing-repair";

    /// <summary>
    /// Historical GST Classification, the one destination that was switchable before this issue
    /// (issue #873). Its stored answer is carried over to this key by
    /// <c>AddPlatformMenuVisibility</c>, and the legacy
    /// <c>historicalGstClassificationVisible</c> field of <c>GET /api/me/access</c> is derived from
    /// it for the duration of the rollout.
    /// </summary>
    public const string HistoricalGstClassification = "admin.historical-gst-classification";

    /// <summary>Nayax Sale Timestamp Repair.</summary>
    public const string NayaxSaleTimestampRepair = "admin.nayax-sale-timestamp-repair";
}

/// <summary>
/// The menu groups the catalogue's destinations belong to (issues #1148 and #1153), named once.
///
/// <para>A group is the menu's own heading for the destinations under it, and it is the
/// <em>display</em> word as well as the classification: the Super Admin surface renders one section
/// per group, so a second copy of that wording in the frontend would be a second chance to get it
/// wrong. Each group's keys are namespaced by its own lower-cased name.</para>
/// </summary>
public static class MenuVisibilityGroups
{
    /// <summary>The business Reports destinations (issue #1153).</summary>
    public const string Reports = "Reports";

    /// <summary>The Admin maintenance destinations (issue #1148).</summary>
    public const string Admin = "Admin";
}

/// <summary>
/// One switchable menu destination as the catalogue declares it (issues #1148 and #1153).
/// </summary>
/// <param name="Key">
/// The server-owned, stable key the stored row and every API response name the destination by. It is
/// namespaced by the menu group it belongs to - <c>reports.</c> or <c>admin.</c> - which is why the
/// Reports group of #1153 needed no change to the storage, which knows nothing about groups.
/// </param>
/// <param name="Group">
/// The menu group the destination sits in, one of <see cref="MenuVisibilityGroups"/>. It is what
/// <c>GET /api/super-admin/menu-visibility</c> reports the destination under and what the Super
/// Admin page renders its sections from.
/// </param>
/// <param name="Label">
/// The destination's own display label, the same words the menu uses, so the Super Admin surface can
/// list the catalogue without keeping a second copy of the menu's wording.
/// </param>
public sealed record MenuVisibilityOption(string Key, string Group, string Label);

/// <summary>
/// The closed catalogue of menu destinations a Super Admin may switch off for everybody else
/// (issues #1148 and #1153).
///
/// <para><strong>Closed and compiled, deliberately not a feature-flag framework.</strong> Issue
/// #873's rule that a second switchable option must be a second named field - and never a key in a
/// map - is superseded here by this catalogue, and the reason that rule existed is honoured rather
/// than abandoned: the keys are a compiled list decided in one reviewed place, a caller cannot
/// invent one, and a key that is not declared here is refused by
/// <see cref="SetMenuVisibility"/> with nothing stored. What changed is only that fifteen
/// destinations do not need fifteen columns.</para>
///
/// <para><strong>Two groups, in menu order.</strong> The eight business Reports destinations
/// (issue #1153) and the seven Admin maintenance destinations (issue #1148), each option naming its
/// own <see cref="MenuVisibilityOption.Group"/> - Reports first, because that is where the menu puts
/// them. The storage knows nothing about groups: a key is namespaced by its group's own name, which
/// is why the Reports half needed no migration at all.</para>
///
/// <para><strong>What is deliberately not in it.</strong> Integrations, Users, Roles and Business
/// Settings are never switchable (owner's decision, 9 November 2026): an Owner needs them to connect
/// Nayax and run their business, so they always follow their normal capability, plan and provider
/// rules. Super Admin tools are never in it either, nor is any destination outside the Reports and
/// Admin groups.</para>
///
/// <para>It decides nothing about <em>authorization</em>. Every destination's endpoints keep their
/// own capability, plan and provider checks in both states; switching one off stops it being
/// offered and grants or revokes nothing. Every report keeps its own capability, plan and provider
/// checks exactly as it had them, and <c>/reports</c> still redirects to Bookkeeping. See
/// docs/architecture.md § Switchable Admin menu visibility.</para>
/// </summary>
public static class MenuVisibilityCatalogue
{
    /// <summary>
    /// Every switchable destination, grouped and in the order the menu declares them, which is the
    /// order the Super Admin surface lists its sections and rows in.
    /// </summary>
    public static IReadOnlyList<MenuVisibilityOption> Options { get; } =
    [
        // Reports first, because that is where the menu puts them: the Super Admin page lists its
        // sections in this order, so the catalogue's order is the one decision about it.
        new(MenuVisibilityKeys.ReportsBookkeeping, MenuVisibilityGroups.Reports, "Bookkeeping"),
        new(MenuVisibilityKeys.ReportsDaily, MenuVisibilityGroups.Reports, "Daily"),
        new(MenuVisibilityKeys.ReportsTransactions, MenuVisibilityGroups.Reports, "Transactions"),
        new(MenuVisibilityKeys.ReportsReconciliation, MenuVisibilityGroups.Reports, "Reconciliation"),
        new(MenuVisibilityKeys.ReportsMachines, MenuVisibilityGroups.Reports, "Machines"),
        new(MenuVisibilityKeys.ReportsProducts, MenuVisibilityGroups.Reports, "Products"),
        new(MenuVisibilityKeys.ReportsSiteCommissions, MenuVisibilityGroups.Reports, "Site Commissions"),
        new(MenuVisibilityKeys.ReportsGst, MenuVisibilityGroups.Reports, "GST"),
        new(MenuVisibilityKeys.SiteCommissionAgreements, MenuVisibilityGroups.Admin, "Site Commission Agreements"),
        new(MenuVisibilityKeys.Imports, MenuVisibilityGroups.Admin, "Imports"),
        new(MenuVisibilityKeys.HistoricalCostRecovery, MenuVisibilityGroups.Admin, "Historical Cost Recovery"),
        new(MenuVisibilityKeys.AvcoTransition, MenuVisibilityGroups.Admin, "AVCO Transition"),
        new(MenuVisibilityKeys.CostingRepair, MenuVisibilityGroups.Admin, "Costing Repair"),
        new(MenuVisibilityKeys.HistoricalGstClassification, MenuVisibilityGroups.Admin, "Historical GST Classification"),
        new(MenuVisibilityKeys.NayaxSaleTimestampRepair, MenuVisibilityGroups.Admin, "Nayax Sale Timestamp Repair"),
    ];

    /// <summary>
    /// The declared destination <paramref name="key"/> names, or <see langword="null"/> when the
    /// catalogue declares no such key - which is the only answer an invented, retired or
    /// differently spelled key gets. The comparison is ordinal: the keys are server-owned constants,
    /// not user input to be interpreted generously.
    /// </summary>
    public static MenuVisibilityOption? Find(string key) =>
        Options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal));
}
