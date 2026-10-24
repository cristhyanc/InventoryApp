using Inventory.Application.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Dashboard;

public class GetInventoryValuationSummaryTests
{
    [Fact]
    public async Task Known_cost_for_every_product_returns_the_real_total()
    {
        var useCase = new GetInventoryValuationSummary(
            FakeInventoryValuationFactsProvider.Stocked(40m, 60m));

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Equal(100m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

    [Fact]
    public async Task Zero_known_cost_is_a_real_zero_not_unavailable()
    {
        var useCase = new GetInventoryValuationSummary(
            FakeInventoryValuationFactsProvider.Stocked(0m, 0m));

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Equal(0m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task Incomplete_costing_makes_the_total_unavailable()
    {
        var useCase = new GetInventoryValuationSummary(
            FakeInventoryValuationFactsProvider.Stocked(40m, null));

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

    [Fact]
    public async Task No_products_reports_a_known_zero_total()
    {
        var useCase = new GetInventoryValuationSummary(new FakeInventoryValuationFactsProvider([]));

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Equal(0m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.TotalProducts);
    }

    /// <summary>
    /// Issue #861 through this use case: the never-stocked product neither adds to the total nor
    /// blocks it, and is still one of the products the catalogue holds. The two counts the tile
    /// publishes are deliberately different figures.
    /// </summary>
    [Fact]
    public async Task A_never_stocked_product_contributes_nothing_and_does_not_block_the_total()
    {
        var useCase = new GetInventoryValuationSummary(new FakeInventoryValuationFactsProvider(
            [FakeInventoryValuationFactsProvider.Stocked(40m), FakeInventoryValuationFactsProvider.NeverStocked()]));

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Equal(40m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }

    [Fact]
    public async Task A_catalogue_of_only_never_stocked_products_is_a_known_zero_total()
    {
        var useCase = new GetInventoryValuationSummary(new FakeInventoryValuationFactsProvider(
            [FakeInventoryValuationFactsProvider.NeverStocked(), FakeInventoryValuationFactsProvider.NeverStocked()]));

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Equal(0m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
        Assert.Equal(2, result.TotalProducts);
    }
}
