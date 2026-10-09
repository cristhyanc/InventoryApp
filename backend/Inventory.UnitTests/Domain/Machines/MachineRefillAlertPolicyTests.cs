using Inventory.Domain.Machines;
using Inventory.Domain.Sites;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

/// <summary>
/// The fleet refill rules behind the home Dashboard's "Needs refill" card (issue #459). The
/// distinct-machine counting is the point of these tests: a machine with several alerting selections,
/// or a product alerting on several machines, must never inflate the machine count, and the low and
/// empty selection counts must stay disjoint so a caller can present them without double counting.
/// </summary>
public class MachineRefillAlertPolicyTests
{
    private static MachineSelectionStockFact Selection(
        long machineId, long? productId, int par, int missing, int threshold, bool isActive = true) =>
        new(machineId, productId, isActive, par, missing, threshold);

    [Fact]
    public void An_empty_fleet_alerts_nothing_and_evaluates_nothing()
    {
        var summary = MachineRefillAlertPolicy.Summarize([]);

        Assert.Equal(0, summary.MachinesNeedingRefill);
        Assert.Equal(0, summary.LowSelectionCount);
        Assert.Equal(0, summary.EmptySelectionCount);
        Assert.Equal(0, summary.SelectionsEvaluated);
    }

    [Theory]
    [InlineData(-1, 2, MachineSelectionStockLevel.Empty)]
    [InlineData(0, 2, MachineSelectionStockLevel.Empty)]
    [InlineData(1, 2, MachineSelectionStockLevel.Low)]
    [InlineData(2, 2, MachineSelectionStockLevel.Low)]
    [InlineData(3, 2, MachineSelectionStockLevel.Stocked)]
    [InlineData(1, 0, MachineSelectionStockLevel.Stocked)]
    public void Classification_is_empty_at_or_below_zero_then_low_up_to_and_including_the_threshold(
        int quantity, int threshold, MachineSelectionStockLevel expected)
    {
        Assert.Equal(expected, MachineRefillAlertPolicy.Classify(quantity, threshold));
    }

    [Fact]
    public void A_machine_with_several_alerting_selections_is_counted_once()
    {
        var summary = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, 100, par: 10, missing: 10, threshold: 2),
            Selection(1, 200, par: 10, missing: 9, threshold: 2),
            Selection(1, 300, par: 10, missing: 0, threshold: 2),
        ]);

        Assert.Equal(1, summary.MachinesNeedingRefill);
        Assert.Equal(1, summary.EmptySelectionCount);
        Assert.Equal(1, summary.LowSelectionCount);
        Assert.Equal(3, summary.SelectionsEvaluated);
    }

    /// <summary>
    /// The two machine counts answer "which machines have an empty/low selection" and overlap by
    /// design; the refill count is the distinct union, never their sum.
    /// </summary>
    [Fact]
    public void A_machine_with_both_a_low_and_an_empty_selection_appears_in_both_machine_counts_but_needs_refill_once()
    {
        var summary = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, 100, par: 10, missing: 10, threshold: 2),
            Selection(1, 200, par: 10, missing: 9, threshold: 2),
        ]);

        Assert.Equal(1, summary.MachinesWithEmptySelections);
        Assert.Equal(1, summary.MachinesWithLowSelections);
        Assert.Equal(1, summary.MachinesNeedingRefill);
    }

    /// <summary>
    /// One product low on three machines is three machine-level alerts, which is exactly why the site
    /// policy's per-product counts cannot be aggregated into this figure.
    /// </summary>
    [Fact]
    public void One_product_alerting_on_several_machines_counts_every_machine()
    {
        var summary = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, 100, par: 10, missing: 9, threshold: 2),
            Selection(2, 100, par: 10, missing: 9, threshold: 2),
            Selection(3, 100, par: 10, missing: 9, threshold: 2),
        ]);

        Assert.Equal(3, summary.MachinesNeedingRefill);
        Assert.Equal(3, summary.LowSelectionCount);
    }

    [Fact]
    public void A_fully_stocked_fleet_needs_no_refill_but_still_reports_what_it_evaluated()
    {
        var summary = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, 100, par: 10, missing: 0, threshold: 2),
            Selection(2, 200, par: 10, missing: 1, threshold: 2),
        ]);

        Assert.Equal(0, summary.MachinesNeedingRefill);
        Assert.Equal(2, summary.SelectionsEvaluated);
    }

    [Fact]
    public void An_unmapped_or_inactive_product_is_never_evaluated_and_never_alerts()
    {
        var summary = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, null, par: 10, missing: 10, threshold: 2),
            Selection(1, 100, par: 10, missing: 10, threshold: 2, isActive: false),
        ]);

        Assert.Equal(0, summary.MachinesNeedingRefill);
        Assert.Equal(0, summary.EmptySelectionCount);
        Assert.Equal(0, summary.SelectionsEvaluated);
    }

    /// <summary>
    /// Several MDB slots carrying one product on one machine are one selection, summed before being
    /// classified - the aggregation the Pick List already applies. Two slots of five with four
    /// missing each is two left of ten, which is low against a combined threshold of four, not two
    /// separate alerts.
    /// </summary>
    [Fact]
    public void Duplicate_mappings_of_one_product_on_one_machine_are_summed_into_a_single_selection()
    {
        var summary = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, 100, par: 5, missing: 4, threshold: 2),
            Selection(1, 100, par: 5, missing: 4, threshold: 2),
        ]);

        Assert.Equal(1, summary.SelectionsEvaluated);
        Assert.Equal(1, summary.LowSelectionCount);
        Assert.Equal(0, summary.EmptySelectionCount);
        Assert.Equal(1, summary.MachinesNeedingRefill);
    }

    /// <summary>
    /// The site dashboard's own low/empty counts and this policy must classify a selection the same
    /// way: both call <see cref="MachineRefillAlertPolicy.Classify"/>, so one on-threshold product on
    /// one machine is "low" in both, and an exhausted one is "empty" in both.
    /// </summary>
    [Fact]
    public void The_site_alert_counts_classify_a_selection_the_same_way()
    {
        var siteFacts = new[]
        {
            new SiteMachineProductStockFact(100, true, Par: 10, MissingStockByMdb: 8, VendOutAlertThreshold: 2),
            new SiteMachineProductStockFact(200, true, Par: 10, MissingStockByMdb: 10, VendOutAlertThreshold: 2),
        };

        var (siteLow, siteEmpty) = SiteStockPolicy.CalculateAlertCounts(siteFacts);
        var fleet = MachineRefillAlertPolicy.Summarize(
        [
            Selection(1, 100, par: 10, missing: 8, threshold: 2),
            Selection(1, 200, par: 10, missing: 10, threshold: 2),
        ]);

        Assert.Equal(siteLow, fleet.LowSelectionCount);
        Assert.Equal(siteEmpty, fleet.EmptySelectionCount);
    }
}
