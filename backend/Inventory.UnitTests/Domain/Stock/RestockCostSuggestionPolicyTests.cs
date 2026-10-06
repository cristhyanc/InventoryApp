using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// The restock-cost-suggestion priority (issue #282): the latest purchase cost, else the average
/// unit cost when valid, else none. Mirrors the former
/// <c>InventoryApi.Services.StockService.GetRestockCostSuggestion</c> decision exactly, given
/// already-queried facts.
/// </summary>
public class RestockCostSuggestionPolicyTests
{
    [Fact]
    public void A_last_purchase_wins_over_a_known_average_cost()
    {
        var lastPurchase = new LastPurchaseCostFact(2.50m, new DateTime(2025, 2, 1));

        var suggestion = RestockCostSuggestionPolicy.Resolve(lastPurchase, costingQuantity: 2, inventoryValue: 8m, averageUnitCost: 4m);

        Assert.Equal(2.50m, suggestion.UnitCost);
        Assert.Equal(RestockCostSuggestionPolicy.LastPurchaseSource, suggestion.Source);
        Assert.Equal(new DateTime(2025, 2, 1), suggestion.PurchaseDate);
    }

    [Fact]
    public void A_known_zero_average_cost_is_used_without_a_purchase()
    {
        var suggestion = RestockCostSuggestionPolicy.Resolve(null, costingQuantity: 3, inventoryValue: 0m, averageUnitCost: 0m);

        Assert.Equal(0m, suggestion.UnitCost);
        Assert.Equal(RestockCostSuggestionPolicy.AverageUnitCostSource, suggestion.Source);
        Assert.Null(suggestion.PurchaseDate);
    }

    [Fact]
    public void An_unknown_costing_quantity_falls_back_to_none()
    {
        var suggestion = RestockCostSuggestionPolicy.Resolve(null, costingQuantity: null, inventoryValue: null, averageUnitCost: 0m);

        Assert.Null(suggestion.UnitCost);
        Assert.Equal(RestockCostSuggestionPolicy.NoneSource, suggestion.Source);
    }

    [Fact]
    public void A_zero_costing_quantity_falls_back_to_none()
    {
        var suggestion = RestockCostSuggestionPolicy.Resolve(null, costingQuantity: 0, inventoryValue: 0m, averageUnitCost: 0m);

        Assert.Equal(RestockCostSuggestionPolicy.NoneSource, suggestion.Source);
    }

    [Fact]
    public void A_missing_inventory_value_falls_back_to_none_even_with_positive_costing_quantity()
    {
        var suggestion = RestockCostSuggestionPolicy.Resolve(null, costingQuantity: 3, inventoryValue: null, averageUnitCost: 1m);

        Assert.Equal(RestockCostSuggestionPolicy.NoneSource, suggestion.Source);
    }

    [Fact]
    public void A_negative_average_unit_cost_falls_back_to_none()
    {
        var suggestion = RestockCostSuggestionPolicy.Resolve(null, costingQuantity: 3, inventoryValue: 1m, averageUnitCost: -1m);

        Assert.Equal(RestockCostSuggestionPolicy.NoneSource, suggestion.Source);
    }
}
