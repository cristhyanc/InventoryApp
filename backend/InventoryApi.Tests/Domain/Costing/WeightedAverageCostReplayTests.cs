using Inventory.Domain.Costing;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Costing;

public class WeightedAverageCostReplayTests
{
    private const long ProductId = 7;
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CostReplayProduct Product(int quantityInStock = 0, int? costingQuantity = 0, decimal? inventoryValue = 0m) =>
        new(ProductId, quantityInStock, costingQuantity, inventoryValue);

    private static CostReplayAdjustment Adjustment(
        int id, int minutes, StockAdjustmentReason reason, int quantityChange, decimal? unitCost = null, bool linked = true) =>
        new(id, T0.AddMinutes(minutes), reason, quantityChange, unitCost, linked);

    private static CostReplaySale Sale(long transactionId, int minutes) => new(transactionId, T0.AddMinutes(minutes));

    private static WeightedAverageCostReplayResult Replay(
        IReadOnlyCollection<CostReplayAdjustment> adjustments,
        IReadOnlyCollection<CostReplaySale>? sales = null,
        CostReplayProduct? product = null,
        CostReplayBaseline? baseline = null,
        long? targetSaleTransactionId = null,
        DateTime? targetSaleTime = null) =>
        WeightedAverageCostReplay.Replay(
            product ?? Product(), adjustments, sales ?? [], baseline, targetSaleTransactionId, targetSaleTime);

    [Fact]
    public void Restocks_and_sales_produce_weighted_average_cost_and_per_sale_cost()
    {
        var result = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 10, 2m), Adjustment(2, 10, StockAdjustmentReason.Restock, 10, 4m)],
            [Sale(100, 5), Sale(101, 20)]);

        Assert.Equal(20, result.PhysicalQuantity);
        Assert.Equal(18, result.CostingQuantity);
        // 10 @ 2 = 20; sale at 2 -> 9 units, 18; +10 @ 4 -> 19 units, 58; sale at 58/19.
        var secondSaleCost = 58m / 19m;
        Assert.Equal(58m - secondSaleCost, result.InventoryValue);
        Assert.Equal((58m - secondSaleCost) / 18m, result.AverageUnitCost);
        Assert.Equal([(100L, 2m), (101L, secondSaleCost)], result.SaleCosts.Select(x => (x.Sale.TransactionId, x.UnitCost)));
        Assert.Empty(result.Issues);
        Assert.False(result.HasFatalIssue);
    }

    [Fact]
    public void Average_unit_cost_and_sale_cost_are_not_rounded()
    {
        var result = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 1, 1m), Adjustment(2, 1, StockAdjustmentReason.Restock, 2, 0m)],
            [Sale(100, 2)]);

        Assert.Equal(1m / 3m, result.Adjustments[1].AverageUnitCostAfter);
        Assert.Equal(1m / 3m, result.SaleCosts.Single().UnitCost);
        Assert.Equal(1m - 1m / 3m, result.InventoryValue);
        Assert.Equal((1m - 1m / 3m) / 2m, result.AverageUnitCost);
    }

    [Fact]
    public void Same_timestamp_ties_replay_restocks_then_machine_refills_then_sales_then_other_movements()
    {
        var result = Replay(
            [
                Adjustment(5, 0, StockAdjustmentReason.Damaged, -1),
                Adjustment(4, 0, StockAdjustmentReason.MachineRefill, -2),
                Adjustment(3, 0, StockAdjustmentReason.Restock, 4, 1m),
            ],
            [Sale(100, 0)]);

        Assert.Equal([3, 4, 5], result.Adjustments.Select(x => x.Adjustment.Id));
        Assert.Equal([4, 2, 1], result.Adjustments.Select(x => x.QuantityAfter));
        // Restock 4, refill keeps costing quantity, sale consumes one, damaged consumes one.
        Assert.Equal([4, 4, 2], result.Adjustments.Select(x => x.CostingQuantityAfter));
        Assert.Equal(1m, result.SaleCosts.Single().UnitCost);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Events_replay_in_timestamp_order_then_by_source_id_regardless_of_input_order()
    {
        var result = Replay(
            [
                Adjustment(9, 2, StockAdjustmentReason.Restock, 1, 9m),
                Adjustment(2, 0, StockAdjustmentReason.Correction, -1),
                Adjustment(1, 0, StockAdjustmentReason.Correction, 1),
                Adjustment(3, -1, StockAdjustmentReason.Restock, 2, 1m),
            ]);

        Assert.Equal([3, 1, 2, 9], result.Adjustments.Select(x => x.Adjustment.Id));
    }

    [Fact]
    public void Machine_refill_changes_physical_stock_only_and_carries_no_cost()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 5, 2m), Adjustment(2, 1, StockAdjustmentReason.MachineRefill, -3)]);

        var refill = result.Adjustments[1];
        Assert.Equal(2, refill.QuantityAfter);
        Assert.Equal(5, refill.CostingQuantityAfter);
        Assert.Equal(10m, refill.InventoryValueAfter);
        Assert.Null(refill.AssignedUnitCost);
        Assert.Null(refill.AssignedTotalCost);
    }

    [Fact]
    public void Restock_assigns_total_cost_and_keeps_its_unit_cost()
    {
        var restock = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 4, 2.5m)]).Adjustments.Single();

        Assert.Null(restock.AssignedUnitCost);
        Assert.Equal(10m, restock.AssignedTotalCost);
    }

    [Theory]
    [InlineData(StockAdjustmentReason.Damaged)]
    [InlineData(StockAdjustmentReason.Expired)]
    [InlineData(StockAdjustmentReason.Sale)]
    public void Cost_bearing_write_off_consumes_at_average_cost(StockAdjustmentReason reason)
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 4, 2m), Adjustment(2, 1, reason, -3)]);

        var writeOff = result.Adjustments[1];
        Assert.Equal(1, writeOff.CostingQuantityAfter);
        Assert.Equal(2m, writeOff.InventoryValueAfter);
        Assert.Equal(2m, writeOff.AssignedUnitCost);
        Assert.Equal(6m, writeOff.AssignedTotalCost);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Positive_and_negative_corrections_are_costed_at_the_current_average()
    {
        var result = Replay(
            [
                Adjustment(1, 0, StockAdjustmentReason.Restock, 4, 2m),
                Adjustment(2, 1, StockAdjustmentReason.Correction, 2),
                Adjustment(3, 2, StockAdjustmentReason.Correction, -3),
            ]);

        Assert.Equal((2m, 4m, 6, 12m), (result.Adjustments[1].AssignedUnitCost, result.Adjustments[1].AssignedTotalCost,
            result.Adjustments[1].CostingQuantityAfter, result.Adjustments[1].InventoryValueAfter));
        Assert.Equal((2m, 6m, 3, 6m), (result.Adjustments[2].AssignedUnitCost, result.Adjustments[2].AssignedTotalCost,
            result.Adjustments[2].CostingQuantityAfter, result.Adjustments[2].InventoryValueAfter));
    }

    [Theory]
    [InlineData(StockAdjustmentReason.Correction, 0)]
    [InlineData(StockAdjustmentReason.Restock, 0)]
    [InlineData(StockAdjustmentReason.Restock, -2)]
    [InlineData(StockAdjustmentReason.Damaged, 0)]
    [InlineData(StockAdjustmentReason.Damaged, 1)]
    [InlineData(StockAdjustmentReason.MachineRefill, 0)]
    public void Zero_or_non_costing_quantity_changes_leave_cost_untouched(StockAdjustmentReason reason, int quantityChange)
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 4, 2m), Adjustment(2, 1, reason, quantityChange, 9m)]);

        var adjustment = result.Adjustments[1];
        Assert.Equal(4 + quantityChange, adjustment.QuantityAfter);
        Assert.Equal(4, adjustment.CostingQuantityAfter);
        Assert.Equal(8m, adjustment.InventoryValueAfter);
        Assert.Null(adjustment.AssignedUnitCost);
        Assert.Null(adjustment.AssignedTotalCost);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Zero_costing_quantity_has_no_average_unit_cost()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 2, 2m)], [Sale(100, 1), Sale(101, 2)]);

        Assert.Equal(0, result.CostingQuantity);
        Assert.Equal(0m, result.InventoryValue);
        Assert.Null(result.AverageUnitCost);
        Assert.Null(WeightedAverageCostReplay.AverageUnitCost(-1, 5m));
        Assert.Equal(2.5m, WeightedAverageCostReplay.AverageUnitCost(2, 5m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-0.01)]
    public void Restock_without_a_valid_purchase_cost_is_a_fatal_unknown_cost(double? unitCost)
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 5, (decimal?)unitCost)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.UnknownCost, issue.Code);
        Assert.Equal($"Restock adjustment 1 for product {ProductId} has no valid unit cost.", issue.Message);
        Assert.True(issue.IsFatal);
        Assert.True(result.HasFatalIssue);
        var restock = result.Adjustments.Single();
        Assert.Equal(5, restock.QuantityAfter);
        Assert.Equal(0, restock.CostingQuantityAfter);
        Assert.Null(restock.AssignedTotalCost);
    }

    [Fact]
    public void Unlinked_costed_restock_is_a_non_fatal_legacy_issue()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 5, 2m, linked: false)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.LegacyUnlinkedCostedRestock, issue.Code);
        Assert.Equal($"Restock adjustment 1 for product {ProductId} is a legacy costed opening/purchase without a receipt item link.", issue.Message);
        Assert.False(issue.IsFatal);
        Assert.False(result.HasFatalIssue);
        Assert.Equal(10m, result.InventoryValue);
    }

    [Fact]
    public void Sale_without_any_costed_acquisition_is_a_missing_opening_and_is_skipped()
    {
        var result = Replay([], [Sale(100, 0)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.MissingOpening, issue.Code);
        Assert.Equal($"Completed Nayax sale 100 for product {ProductId} has no known opening cost at {T0:O}.", issue.Message);
        Assert.True(issue.IsFatal);
        Assert.Equal(0, result.CostingQuantity);
        Assert.Empty(result.SaleCosts);
    }

    [Fact]
    public void Sale_after_stock_is_exhausted_is_an_unknown_cost()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 1, 2m)], [Sale(100, 1), Sale(101, 2)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.UnknownCost, issue.Code);
        Assert.Single(result.SaleCosts);
    }

    [Fact]
    public void Correction_without_known_average_is_missing_opening_or_unknown_cost()
    {
        var noAcquisition = Replay([Adjustment(1, 0, StockAdjustmentReason.Correction, 2)]);
        var issue = Assert.Single(noAcquisition.Issues);
        Assert.Equal(CostDataQualityIssueCodes.MissingOpening, issue.Code);
        Assert.Equal($"Correction adjustment 1 for product {ProductId} has no known cost.", issue.Message);
        Assert.Equal(0, noAcquisition.CostingQuantity);
        Assert.Null(noAcquisition.Adjustments.Single().AssignedUnitCost);

        var exhausted = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 1, 2m), Adjustment(2, 1, StockAdjustmentReason.Damaged, -1), Adjustment(3, 2, StockAdjustmentReason.Correction, 1)]);
        Assert.Equal(CostDataQualityIssueCodes.UnknownCost, Assert.Single(exhausted.Issues).Code);
    }

    [Fact]
    public void Write_off_without_known_average_reports_issue_and_negative_costing_stock()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Expired, -2)]);

        Assert.Equal(
            [
                (CostDataQualityIssueCodes.NegativePhysicalStock, $"Product {ProductId} becomes physically negative at stock adjustment 1."),
                (CostDataQualityIssueCodes.MissingOpening, $"Expired adjustment 1 for product {ProductId} has no known cost."),
                (CostDataQualityIssueCodes.NegativeCostingStock, $"Product {ProductId} becomes cost-negative at adjustment 1."),
            ],
            result.Issues.Select(x => (x.Code, x.Message)));
        Assert.All(result.Issues, x => Assert.True(x.IsFatal));
        Assert.Equal(-2, result.CostingQuantity);
        Assert.Equal(0m, result.InventoryValue);
        Assert.Null(result.Adjustments.Single().AssignedUnitCost);
    }

    [Fact]
    public void Negative_correction_beyond_costing_quantity_is_negative_costing_stock()
    {
        var result = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 2, 3m), Adjustment(2, 1, StockAdjustmentReason.MachineRefill, 5), Adjustment(3, 2, StockAdjustmentReason.Correction, -3)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.NegativeCostingStock, issue.Code);
        Assert.Equal($"Product {ProductId} becomes cost-negative at adjustment 3.", issue.Message);
        Assert.Equal(-1, result.CostingQuantity);
        Assert.Equal(-3m, result.InventoryValue);
        Assert.Null(result.AverageUnitCost);
    }

    [Fact]
    public void Physical_stock_going_negative_is_reported()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 1, 1m), Adjustment(2, 1, StockAdjustmentReason.MachineRefill, -2)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.NegativePhysicalStock, issue.Code);
        Assert.True(issue.IsFatal);
        Assert.Equal(-1, result.PhysicalQuantity);
    }

    [Fact]
    public void Stored_physical_stock_without_any_history_or_costing_state_is_missing_opening()
    {
        var result = Replay([], product: Product(quantityInStock: 3, costingQuantity: null, inventoryValue: null));

        Assert.Equal(
            [
                (CostDataQualityIssueCodes.MissingOpening, $"Product {ProductId} has physical stock but no opening stock movement."),
                (CostDataQualityIssueCodes.MissingOpening, $"Product {ProductId} stored physical quantity 3 does not reconcile to its movement history (0)."),
            ],
            result.Issues.Select(x => (x.Code, x.Message)));
    }

    [Fact]
    public void Stored_physical_stock_that_does_not_reconcile_to_history_is_missing_opening()
    {
        var result = Replay([Adjustment(1, 0, StockAdjustmentReason.Restock, 2, 1m)], product: Product(5, null, null));

        var issue = Assert.Single(result.Issues);
        Assert.Equal($"Product {ProductId} stored physical quantity 5 does not reconcile to its movement history (2).", issue.Message);
    }

    [Fact]
    public void Stored_costing_state_or_a_baseline_suppresses_the_opening_reconciliation_issues()
    {
        Assert.Empty(Replay([], product: Product(3, 3, null)).Issues);
        Assert.Empty(Replay([], product: Product(3, null, 6m)).Issues);
        Assert.Empty(Replay([], product: Product(3, null, null), baseline: new CostReplayBaseline(T0, 0, 0, 0m)).Issues);
    }

    [Fact]
    public void Baseline_opens_the_replay_and_events_at_or_before_its_cutoff_are_ignored()
    {
        var baseline = new CostReplayBaseline(T0.AddMinutes(10), HomeStockQuantity: 6, OpeningCostingQuantity: 8, InventoryValue: 16m);

        var result = Replay(
            [Adjustment(1, 5, StockAdjustmentReason.Restock, 100, 99m), Adjustment(2, 10, StockAdjustmentReason.Restock, 100, 99m),
             Adjustment(3, 11, StockAdjustmentReason.Restock, 2, 5m)],
            [Sale(100, 10), Sale(101, 12)],
            baseline: baseline);

        Assert.Equal([3], result.Adjustments.Select(x => x.Adjustment.Id));
        Assert.Equal(8, result.PhysicalQuantity);
        Assert.Equal(26m / 10m, result.SaleCosts.Single().UnitCost);
        Assert.Equal(9, result.CostingQuantity);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Baseline_counts_as_a_costed_acquisition_for_issue_classification()
    {
        var result = Replay([], [Sale(100, 1)], baseline: new CostReplayBaseline(T0, 0, 0, 0m));

        Assert.Equal(CostDataQualityIssueCodes.UnknownCost, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Target_sale_reports_the_average_before_it_and_stops_the_replay()
    {
        var result = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 2, 2m), Adjustment(2, 2, StockAdjustmentReason.Restock, 2, 6m)],
            [Sale(100, 1), Sale(101, 3), Sale(102, 4)],
            targetSaleTransactionId: 101,
            targetSaleTime: T0.AddMinutes(3));

        Assert.Equal(14m / 3m, result.TargetSaleUnitCost);
        Assert.Equal([100L], result.SaleCosts.Select(x => x.Sale.TransactionId));
        Assert.Equal(3, result.CostingQuantity);
    }

    [Fact]
    public void Target_time_without_transaction_stops_at_the_first_sale_at_that_time_and_ignores_later_events()
    {
        var atTime = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 2, 2m)],
            [Sale(100, 1), Sale(101, 1)],
            targetSaleTime: T0.AddMinutes(1));
        Assert.Equal(2m, atTime.TargetSaleUnitCost);
        Assert.Empty(atTime.SaleCosts);

        var noSaleAtTime = Replay(
            [Adjustment(1, 0, StockAdjustmentReason.Restock, 2, 2m), Adjustment(2, 5, StockAdjustmentReason.Restock, 2, 10m)],
            [Sale(100, 1)],
            targetSaleTime: T0.AddMinutes(3));
        Assert.Null(noSaleAtTime.TargetSaleUnitCost);
        Assert.Single(noSaleAtTime.Adjustments);
        Assert.Equal(2m, noSaleAtTime.AverageUnitCost);
    }

    [Fact]
    public void Physical_quantity_overflow_throws()
    {
        Assert.Throws<OverflowException>(() => Replay(
            [Adjustment(1, 0, StockAdjustmentReason.MachineRefill, int.MaxValue), Adjustment(2, 1, StockAdjustmentReason.MachineRefill, 1)]));
    }

    [Theory]
    [InlineData("MissingOpening", true)]
    [InlineData("UnknownCost", true)]
    [InlineData("NegativePhysicalStock", true)]
    [InlineData("NegativeCostingStock", true)]
    [InlineData("LegacyUnlinkedCostedRestock", false)]
    [InlineData("Other", false)]
    public void Fatal_classification_matches_the_established_split(string code, bool fatal)
    {
        Assert.Equal(fatal, CostDataQualityIssueCodes.IsFatal(code));
        Assert.Equal(fatal, new CostDataQualityIssue(code, "message").IsFatal);
    }
}
