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
}
