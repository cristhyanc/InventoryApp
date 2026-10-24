using Inventory.Domain.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Dashboard;

public class InventoryValuationPolicyTests
{
    /// <summary>A product holding 6 units in storage, with a recorded history, valued as given.</summary>
    private static ProductInventoryValuationFact Stocked(decimal? inventoryValue) =>
        new(inventoryValue, CostingQuantity: 6, QuantityInStock: 6, HasInventoryHistory: true);

    /// <summary>
    /// A product exactly as a create leaves it: no established valuation, no costing position, no
    /// storage units, and no recorded movement, purchase, sale, count, baseline or repair.
    /// </summary>
    private static ProductInventoryValuationFact NeverStocked() =>
        new(InventoryValue: null, CostingQuantity: null, QuantityInStock: 0, HasInventoryHistory: false);

    [Fact]
    public void All_products_with_known_cost_sums_to_a_real_total()
    {
        var result = InventoryValuationPolicy.Summarize(
            [Stocked(10m), Stocked(25.50m), Stocked(0m)]);

        Assert.Equal(35.50m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(3, result.TotalProducts);
    }

    [Fact]
    public void No_products_is_a_known_zero_total()
    {
        var result = InventoryValuationPolicy.Summarize([]);

        Assert.Equal(0m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(0, result.TotalProducts);
    }

    [Fact]
    public void Any_product_with_unknown_cost_makes_the_total_unavailable_rather_than_a_partial_sum()
    {
        var result = InventoryValuationPolicy.Summarize([Stocked(10m), Stocked(null), Stocked(5m)]);

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ProductsWithUnknownCost);
        Assert.Equal(3, result.TotalProducts);
    }

    [Fact]
    public void All_products_unknown_reports_every_product_as_missing_cost()
    {
        var result = InventoryValuationPolicy.Summarize([Stocked(null), Stocked(null)]);

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

    /// <summary>
    /// Issue #861: a product nothing has ever put inventory into owns nothing, so it adds nothing to
    /// the total and is not missing cost data - there is no cost for it to be missing. It is still
    /// counted in <c>TotalProducts</c>, because it is in the catalogue: how many products a business
    /// has and how many block its valuation are deliberately separate figures.
    /// </summary>
    [Fact]
    public void A_product_that_has_never_held_inventory_contributes_no_value_and_no_missing_cost()
    {
        var result = InventoryValuationPolicy.Summarize([Stocked(30m), NeverStocked()]);

        Assert.Equal(30m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

    /// <summary>
    /// A catalogue of nothing but never-stocked products is a measured <c>$0.00</c>: the business
    /// owns no inventory, which is an answer rather than a gap.
    /// </summary>
    [Fact]
    public void A_catalogue_of_only_never_stocked_products_is_a_known_zero_total()
    {
        var result = InventoryValuationPolicy.Summarize([NeverStocked(), NeverStocked(), NeverStocked()]);

        Assert.Equal(0m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(3, result.TotalProducts);
    }

    /// <summary>
    /// The exception is about evidence, not about how empty the storage shelf looks. Each of these is
    /// a product with <c>0</c> storage units and no established valuation that must still make the
    /// total unavailable, because something records inventory it owns or owned.
    /// </summary>
    [Theory]
    // Units already loaded into a machine: storage is empty and the business still owns them.
    [InlineData(null, 0, true)]
    // A costing position the rebuild established without a value is unresolved, not empty.
    [InlineData(6, 0, false)]
    // Costing units and a history, with the value still unknown.
    [InlineData(6, 0, true)]
    public void Zero_storage_stock_alone_is_not_evidence_that_a_product_is_empty(
        int? costingQuantity, int quantityInStock, bool hasInventoryHistory)
    {
        var result = InventoryValuationPolicy.Summarize(
        [
            Stocked(30m),
            new(InventoryValue: null, costingQuantity, quantityInStock, hasInventoryHistory),
        ]);

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

    /// <summary>
    /// Storage stock with no established valuation is the original unknown-inventory case and is
    /// unaffected: a product holding units nothing has valued still blocks the total, history or no
    /// history.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Storage_stock_with_no_established_value_still_blocks_the_total(bool hasInventoryHistory)
    {
        var result = InventoryValuationPolicy.Summarize(
            [new(InventoryValue: null, CostingQuantity: null, QuantityInStock: 4, hasInventoryHistory)]);

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ProductsWithUnknownCost);
    }

    /// <summary>
    /// A product whose rebuild established a value is valued on that value alone. Its history is
    /// neither read nor relevant: a measured <c>$0.00</c> position stays a real zero in the total.
    /// </summary>
    [Fact]
    public void An_established_value_is_summed_whatever_the_products_history_says()
    {
        var result = InventoryValuationPolicy.Summarize(
        [
            new(InventoryValue: 0m, CostingQuantity: 0, QuantityInStock: 0, HasInventoryHistory: false),
            new(InventoryValue: 12m, CostingQuantity: 4, QuantityInStock: 4, HasInventoryHistory: true),
        ]);

        Assert.Equal(12m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
    }

    /// <summary>
    /// The stored-column half of the rule, which a persistence adapter reads to decide which products
    /// it still has to ask about the history of. Anything that establishes a position - a value, a
    /// costing quantity or storage units - closes the question on its own.
    /// </summary>
    [Theory]
    [InlineData(null, null, 0, true)]
    [InlineData(null, 0, 0, true)]
    [InlineData(null, null, 1, false)]
    [InlineData(null, 1, 0, false)]
    [InlineData(0d, null, 0, false)]
    [InlineData(9.99d, null, 0, false)]
    public void The_stored_position_decides_which_products_could_still_be_empty(
        double? inventoryValue, int? costingQuantity, int quantityInStock, bool expected) =>
        Assert.Equal(
            expected,
            ProductInventoryValuationFact.CouldNeverHaveHeldInventory(
                inventoryValue is null ? null : (decimal)inventoryValue.Value,
                costingQuantity,
                quantityInStock));

    /// <summary>
    /// One product's display cost (issue #788) is the same completeness rule the total above
    /// applies, asked about a single row: a null <c>InventoryValue</c> is the evidence that no cost
    /// rebuild has ever run for that product, so there is no cost to display. It is deliberately
    /// not a second AVCO calculation - the number returned is the persisted
    /// <c>AverageUnitCost</c>, unchanged.
    /// </summary>
    [Fact]
    public void A_product_no_cost_rebuild_has_run_for_has_no_display_unit_cost() =>
        Assert.Null(InventoryValuationPolicy.DisplayUnitCost(inventoryValue: null, averageUnitCost: 0m));

    /// <summary>
    /// The failure mode this rule exists to prevent: a product whose cost was never rebuilt can
    /// still carry a stored <c>AverageUnitCost</c> - the column is non-nullable and a create can
    /// seed it - and publishing that as the product's cost would present an unverified figure as a
    /// costed one. The evidence is the inventory value, never the average cost itself.
    /// </summary>
    [Fact]
    public void A_stored_average_cost_without_a_rebuild_is_still_unknown() =>
        Assert.Null(InventoryValuationPolicy.DisplayUnitCost(inventoryValue: null, averageUnitCost: 1.25m));

    /// <summary>
    /// A genuine zero survives: a rebuild that found no cost-bearing stock values the product at
    /// $0, which is a measured answer and must not become "unknown".
    /// </summary>
    [Fact]
    public void A_rebuilt_zero_cost_is_a_known_zero_display_unit_cost() =>
        Assert.Equal(0m, InventoryValuationPolicy.DisplayUnitCost(inventoryValue: 0m, averageUnitCost: 0m));

    [Fact]
    public void A_rebuilt_cost_is_the_persisted_average_unit_cost_unchanged() =>
        Assert.Equal(1.25m, InventoryValuationPolicy.DisplayUnitCost(inventoryValue: 8.75m, averageUnitCost: 1.25m));

    /// <summary>
    /// Issue #861 deliberately does not reach this rule: a never-stocked product is excluded from the
    /// catalogue total, and its unit cost stays <em>unknown</em> rather than becoming a fabricated
    /// <c>$0.00</c>. "We own none" is a measurement of a holding; it is not a measurement of what one
    /// unit costs.
    /// </summary>
    [Fact]
    public void A_never_stocked_products_unit_cost_stays_unknown_rather_than_a_fabricated_zero()
    {
        var neverStocked = NeverStocked();

        Assert.True(neverStocked.HasNeverHeldInventory);
        Assert.Null(InventoryValuationPolicy.DisplayUnitCost(
            neverStocked.InventoryValue, averageUnitCost: 1.25m));
    }
}
