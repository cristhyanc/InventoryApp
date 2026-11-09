using Inventory.Application.PlatformOptions;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformOptions;

/// <summary>
/// The closed catalogue of switchable menu destinations (issue #1148).
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
    /// The seven Admin keys of the acceptance criterion, in the order the Admin menu declares them.
    /// Reports are deliberately absent (owner's decision, 9 November 2026, tracked in #1153), and so
    /// are Integrations, Users, Roles and Business Settings, which are never switchable.
    /// </summary>
    [Fact]
    public void The_catalogue_is_exactly_the_seven_switchable_admin_destinations() =>
        Assert.Equal(
            [
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
    /// The keys are namespaced by the menu group they belong to, which is the shape that lets the
    /// Reports group of #1153 arrive as <c>reports.*</c> keys with no storage change at all: the
    /// table is keyed by this string and knows nothing about groups.
    /// </summary>
    [Fact]
    public void Every_key_is_namespaced_under_the_admin_group() =>
        Assert.All(
            MenuVisibilityCatalogue.Options,
            option => Assert.StartsWith("admin.", option.Key, StringComparison.Ordinal));

    /// <summary>A key the catalogue declares is found; anything else is not.</summary>
    [Theory]
    [InlineData("admin.imports", true)]
    [InlineData("admin.historical-gst-classification", true)]
    // The four Admin destinations that are deliberately never switchable.
    [InlineData("admin.users", false)]
    [InlineData("admin.roles", false)]
    [InlineData("admin.business-settings", false)]
    [InlineData("admin.nayax-settings", false)]
    // Reports, which #1153 will decide and this release does not offer.
    [InlineData("reports.sales", false)]
    // A caller cannot reach a key by spelling it differently either: the keys are ordinal.
    [InlineData("Admin.Imports", false)]
    [InlineData("admin.imports ", false)]
    [InlineData("", false)]
    public void Only_a_declared_key_is_found(string key, bool expected) =>
        Assert.Equal(expected, MenuVisibilityCatalogue.Find(key) is not null);
}
