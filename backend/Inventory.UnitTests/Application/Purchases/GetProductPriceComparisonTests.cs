using Inventory.Application.Purchases;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

public class GetProductPriceComparisonTests
{
    [Fact]
    public async Task No_history_returns_no_lowest_or_latest_and_an_empty_history()
    {
        var useCase = new GetProductPriceComparison(
            new FakeProductPurchasePriceHistoryProvider(Array.Empty<SupplierPriceHistoryFact>()));

        var result = await useCase.Handle(1, CancellationToken.None);

        Assert.Null(result.Lowest);
        Assert.Null(result.Latest);
        Assert.Empty(result.HistoryNewestFirst);
    }

    [Fact]
    public async Task Handle_maps_facts_through_the_domain_policy_into_the_dto()
    {
        var useCase = new GetProductPriceComparison(new FakeProductPurchasePriceHistoryProvider(new[]
        {
            new SupplierPriceHistoryFact(1, 10, "January order", new DateTime(2026, 1, 5), 1, "Acme Supplies", 2.00m),
            new SupplierPriceHistoryFact(2, 11, "March order", new DateTime(2026, 3, 5), 2, "Best Wholesale", 1.50m)
        }));

        var result = await useCase.Handle(1, CancellationToken.None);

        Assert.Equal(1.50m, result.Lowest!.UnitCost);
        Assert.Equal("Best Wholesale", result.Lowest.SupplierName);
        Assert.Equal(11, result.Lowest.PurchaseId);

        Assert.Equal(1.50m, result.Latest!.UnitCost);
        Assert.Equal("March order", result.Latest.PurchaseTitle);

        Assert.Equal(0m, result.AbsoluteDifference);
        Assert.True(result.PercentageIsMeaningful);
        Assert.Equal(2, result.HistoryNewestFirst.Count);
        Assert.Equal(2, result.HistoryNewestFirst[0].PurchaseItemId);
    }

    [Fact]
    public async Task Missing_supplier_is_reported_as_a_null_source_rather_than_omitted()
    {
        var useCase = new GetProductPriceComparison(new FakeProductPurchasePriceHistoryProvider(new[]
        {
            new SupplierPriceHistoryFact(1, 10, "No supplier recorded", new DateTime(2026, 1, 5), null, null, 3.00m)
        }));

        var result = await useCase.Handle(1, CancellationToken.None);

        Assert.Null(result.Lowest!.SupplierId);
        Assert.Null(result.Lowest.SupplierName);
        Assert.Single(result.HistoryNewestFirst);
    }

    [Fact]
    public async Task Zero_lowest_cost_does_not_report_a_misleading_percentage()
    {
        var useCase = new GetProductPriceComparison(new FakeProductPurchasePriceHistoryProvider(new[]
        {
            new SupplierPriceHistoryFact(1, 10, "Free promotional stock", new DateTime(2026, 1, 1), 1, "Acme", 0.00m),
            new SupplierPriceHistoryFact(2, 11, "Regular order", new DateTime(2026, 2, 1), 1, "Acme", 2.00m)
        }));

        var result = await useCase.Handle(1, CancellationToken.None);

        Assert.False(result.PercentageIsMeaningful);
        Assert.Null(result.PercentageDifference);
        Assert.Equal(2.00m, result.AbsoluteDifference);
    }
}
