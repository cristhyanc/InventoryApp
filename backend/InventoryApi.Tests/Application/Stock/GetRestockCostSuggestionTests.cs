using Inventory.Application.Stock;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Application.Stock;

public class GetRestockCostSuggestionTests
{
    [Fact]
    public async Task An_unknown_product_returns_null()
    {
        var store = new FakeStockAdjustmentStore { ProductExists = true, RestockCostFacts = null };
        var useCase = new GetRestockCostSuggestion(store);

        var suggestion = await useCase.Handle(1, CancellationToken.None);

        Assert.Null(suggestion);
    }

    [Fact]
    public async Task A_known_product_resolves_the_authoritative_priority()
    {
        var lastPurchase = new LastPurchaseCostFact(2.5m, new DateTime(2025, 2, 1));
        var store = new FakeStockAdjustmentStore
        {
            RestockCostFacts = new RestockCostFacts(lastPurchase, 2, 8m, 4m)
        };
        var useCase = new GetRestockCostSuggestion(store);

        var suggestion = await useCase.Handle(1, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(2.5m, suggestion!.UnitCost);
        Assert.Equal(RestockCostSuggestionPolicy.LastPurchaseSource, suggestion.Source);
    }
}
