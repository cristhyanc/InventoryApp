using Inventory.Application.PlatformOptions;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformOptions;

/// <summary>
/// The closed catalogue of switchable menu destinations (issues #1148 and #1153).
///
/// <para>It is a <em>compiled</em> list rather than a feature-flag framework: a caller cannot invent
/// a key, and which keys exist is a reviewed decision in one place. This file pins the list
/// literally, because the whole point of a closed catalogue is that adding to it or renaming
/// anything in it is a visible change - a renamed key would silently switch a destination back on
/// for every installation that had switched it off.</para>
/// </summary>
public class MenuVisibilityCatalogueTests
{
    /// <summary>
    /// The eight Reports keys of issue #1153 and the seven Admin keys of issue #1148, in the order
    /// the menu declares them - Reports above Admin, each group in its own menu order, which is the
    /// order the Super Admin page lists its sections and rows in. Integrations, Users, Roles and
    /// Business Settings are deliberately absent, being never switchable.
    /// </summary>
    [Fact]
    public void The_catalogue_is_exactly_the_switchable_reports_and_admin_destinations() =>
        Assert.Equal(
            [
                "reports.bookkeeping",
                "reports.daily",
                "reports.transactions",
                "reports.reconciliation",
                "reports.machines",
                "reports.products",
                "reports.site-commissions",
                "reports.gst",
                "admin.site-commission-agreements",
                "admin.imports",
                "admin.historical-cost-recovery",
                "admin.avco-transition",
                "admin.costing-repair",
                "admin.historical-gst-classification",
                "admin.nayax-sale-timestamp-repair",
            ],
            MenuVisibilityCatalogue.Options.Select(option => option.Key).ToArray());

    /// <summary>Every key carries the destination's own display label, and none is blank.</summary>
    [Fact]
    public void Every_key_carries_the_destinations_display_label() =>
        Assert.Equal(
            [
                "Bookkeeping",
                "Daily",
                "Transactions",
                "Reconciliation",
                "Machines",
                "Products",
                "Site Commissions",
                "GST",
                "Site Commission Agreements",
                "Imports",
                "Historical Cost Recovery",
                "AVCO Transition",
                "Costing Repair",
                "Historical GST Classification",
                "Nayax Sale Timestamp Repair",
            ],
            MenuVisibilityCatalogue.Options.Select(option => option.Label).ToArray());

    /// <summary>
    /// Every key carries the menu group it belongs to (issue #1153), which is what
    /// <c>GET /api/super-admin/menu-visibility</c> reports it under and what the Super Admin page
    /// renders its sections from - Reports first, Admin second, each group contiguous, so the page
    /// needs no second copy of either the grouping or its wording.
    /// </summary>
    [Fact]
    public void Every_key_carries_its_menu_group_and_the_groups_are_contiguous()
    {
        Assert.Equal(
            [
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Reports,
                MenuVisibilityGroups.Admin,
                MenuVisibilityGroups.Admin,
                MenuVisibilityGroups.Admin,
                MenuVisibilityGroups.Admin,
                MenuVisibilityGroups.Admin,
                MenuVisibilityGroups.Admin,
                MenuVisibilityGroups.Admin,
            ],
            MenuVisibilityCatalogue.Options.Select(option => option.Group).ToArray());

        Assert.Equal(
            [MenuVisibilityGroups.Reports, MenuVisibilityGroups.Admin],
            MenuVisibilityCatalogue.Options.Select(option => option.Group).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The keys are unique. A duplicate would make one destination's state depend on which entry a
    /// reader happened to find first, and the stored row would be written twice over.
    /// </summary>
    [Fact]
    public void The_keys_are_unique() =>
        Assert.Equal(
            MenuVisibilityCatalogue.Options.Count,
            MenuVisibilityCatalogue.Options.Select(option => option.Key).Distinct(StringComparer.Ordinal).Count());

    /// <summary>
    /// Every key is named in the <see cref="MenuVisibilityKeys"/> constants, which is what the rest
    /// of the application refers to a destination by - never a literal string spelled a second time.
    /// </summary>
    [Fact]
    public void The_constants_and_the_catalogue_are_the_same_list() =>
        Assert.Equal(
            [
                MenuVisibilityKeys.ReportsBookkeeping,
                MenuVisibilityKeys.ReportsDaily,
                MenuVisibilityKeys.ReportsTransactions,
                MenuVisibilityKeys.ReportsReconciliation,
                MenuVisibilityKeys.ReportsMachines,
                MenuVisibilityKeys.ReportsProducts,
                MenuVisibilityKeys.ReportsSiteCommissions,
                MenuVisibilityKeys.ReportsGst,
                MenuVisibilityKeys.SiteCommissionAgreements,
                MenuVisibilityKeys.Imports,
                MenuVisibilityKeys.HistoricalCostRecovery,
                MenuVisibilityKeys.AvcoTransition,
                MenuVisibilityKeys.CostingRepair,
                MenuVisibilityKeys.HistoricalGstClassification,
                MenuVisibilityKeys.NayaxSaleTimestampRepair,
            ],
            MenuVisibilityCatalogue.Options.Select(option => option.Key).ToArray());

    /// <summary>
    /// Every key is namespaced by the menu group it belongs to, which is the shape that let the
    /// Reports group of #1153 arrive as <c>reports.*</c> keys with no storage change at all: the
    /// table is keyed by this string and knows nothing about groups.
    /// </summary>
    [Fact]
    public void Every_key_is_namespaced_under_its_own_group() =>
        Assert.All(
            MenuVisibilityCatalogue.Options,
            option => Assert.StartsWith(
                $"{option.Group.ToLowerInvariant()}.",
                option.Key,
                StringComparison.Ordinal));

    /// <summary>A key the catalogue declares is found; anything else is not.</summary>
    [Theory]
    [InlineData("admin.imports", true)]
    [InlineData("admin.historical-gst-classification", true)]
    [InlineData("reports.bookkeeping", true)]
    [InlineData("reports.site-commissions", true)]
    [InlineData("reports.gst", true)]
    // The four Admin destinations that are deliberately never switchable.
    [InlineData("admin.users", false)]
    [InlineData("admin.roles", false)]
    [InlineData("admin.business-settings", false)]
    [InlineData("admin.nayax-settings", false)]
    // A report the Reports group does not hold: there is no such destination to switch.
    [InlineData("reports.sales", false)]
    // A caller cannot reach a key by spelling it differently either: the keys are ordinal.
    [InlineData("Admin.Imports", false)]
    [InlineData("Reports.GST", false)]
    [InlineData("admin.imports ", false)]
    [InlineData("", false)]
    public void Only_a_declared_key_is_found(string key, bool expected) =>
        Assert.Equal(expected, MenuVisibilityCatalogue.Find(key) is not null);
}
