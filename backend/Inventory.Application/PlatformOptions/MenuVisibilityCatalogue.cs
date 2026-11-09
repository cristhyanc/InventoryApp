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
/// </summary>
public static class MenuVisibilityKeys
{
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
/// One switchable menu destination as the catalogue declares it (issue #1148).
/// </summary>
/// <param name="Key">
/// The server-owned, stable key the stored row and every API response name the destination by. It is
/// namespaced by the menu group it belongs to - <c>admin.</c> today - so a <c>reports.</c> group can
/// be added by #1153 with no change to the storage, which knows nothing about groups.
/// </param>
/// <param name="Label">
/// The destination's own display label, the same words the Admin menu uses, so the Super Admin
/// surface can list the catalogue without keeping a second copy of the menu's wording.
/// </param>
public sealed record MenuVisibilityOption(string Key, string Label);

/// <summary>
/// The closed catalogue of menu destinations a Super Admin may switch off for everybody else
/// (issue #1148).
///
/// <para><strong>Closed and compiled, deliberately not a feature-flag framework.</strong> Issue
/// #873's rule that a second switchable option must be a second named field - and never a key in a
/// map - is superseded here by this catalogue, and the reason that rule existed is honoured rather
/// than abandoned: the keys are a compiled list decided in one reviewed place, a caller cannot
/// invent one, and a key that is not declared here is refused by
/// <see cref="SetMenuVisibility"/> with nothing stored. What changed is only that seven
/// destinations do not need seven columns.</para>
///
/// <para><strong>What is deliberately not in it.</strong> Integrations, Users, Roles and Business
/// Settings are never switchable (owner's decision, 9 November 2026): an Owner needs them to connect
/// Nayax and run their business, so they always follow their normal capability, plan and provider
/// rules. Super Admin tools are never in it either, nor is any destination outside the Admin group.
/// Reports are out for now and are tracked in #1153.</para>
///
/// <para>It decides nothing about <em>authorization</em>. Every destination's endpoints keep their
/// own capability, plan and provider checks in both states; switching one off stops it being
/// offered and grants or revokes nothing. See docs/architecture.md § Switchable Admin menu
/// visibility.</para>
/// </summary>
public static class MenuVisibilityCatalogue
{
    /// <summary>
    /// Every switchable destination, in the order the Admin menu declares them, which is the order
    /// the Super Admin surface lists them in.
    /// </summary>
    public static IReadOnlyList<MenuVisibilityOption> Options { get; } =
    [
        new(MenuVisibilityKeys.SiteCommissionAgreements, "Site Commission Agreements"),
        new(MenuVisibilityKeys.Imports, "Imports"),
        new(MenuVisibilityKeys.HistoricalCostRecovery, "Historical Cost Recovery"),
        new(MenuVisibilityKeys.AvcoTransition, "AVCO Transition"),
        new(MenuVisibilityKeys.CostingRepair, "Costing Repair"),
        new(MenuVisibilityKeys.HistoricalGstClassification, "Historical GST Classification"),
        new(MenuVisibilityKeys.NayaxSaleTimestampRepair, "Nayax Sale Timestamp Repair"),
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
