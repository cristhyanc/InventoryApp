using Inventory.Domain.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Dashboard;

public class InventoryValuationPolicyTests
{
    [Fact]
    public void All_products_with_known_cost_sums_to_a_real_total()
    {
        var result = InventoryValuationPolicy.Summarize(new decimal?[] { 10m, 25.50m, 0m });

        Assert.Equal(35.50m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(3, result.TotalProducts);
    }

    [Fact]
    public void No_products_is_a_known_zero_total()
    {
        var result = InventoryValuationPolicy.Summarize(Array.Empty<decimal?>());

        Assert.Equal(0m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(0, result.TotalProducts);
    }

    [Fact]
    public void Any_product_with_unknown_cost_makes_the_total_unavailable_rather_than_a_partial_sum()
    {
        var result = InventoryValuationPolicy.Summarize(new decimal?[] { 10m, null, 5m });

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ProductsWithUnknownCost);
        Assert.Equal(3, result.TotalProducts);
    }

    [Fact]
    public void All_products_unknown_reports_every_product_as_missing_cost()
    {
        var result = InventoryValuationPolicy.Summarize(new decimal?[] { null, null });

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

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
}
