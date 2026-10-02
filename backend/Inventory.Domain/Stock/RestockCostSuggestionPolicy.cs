namespace Inventory.Domain.Stock;

/// <summary>The latest purchase unit cost available for a product, already queried by the caller.</summary>
public sealed record LastPurchaseCostFact(decimal UnitCost, DateTime PurchaseDate);

/// <summary>
/// One authoritative restock-cost suggestion (issue #282): a decimal unit cost (absent when none is
/// available), the fact it was derived from, and the purchase date when it came from a purchase.
/// <see cref="Source"/> values are serialized verbatim in the API response
/// (<c>RestockCostSuggestionDto.Source</c>) and must not change.
/// </summary>
public sealed record RestockCostSuggestion(decimal? UnitCost, string Source, DateTime? PurchaseDate);

/// <summary>
/// The one authoritative restock-cost-suggestion priority: the latest purchase unit cost when one
/// exists, otherwise the product's average unit cost when its costing facts are valid, otherwise
/// none. Mirrors the former
/// <c>InventoryApi.Services.StockService.GetRestockCostSuggestion</c> decision exactly, given
/// already-queried facts - it never reads persisted state itself.
/// </summary>
public static class RestockCostSuggestionPolicy
{
    public const string LastPurchaseSource = "LastPurchase";
    public const string AverageUnitCostSource = "AverageUnitCost";
    public const string NoneSource = "None";

    public static RestockCostSuggestion Resolve(
        LastPurchaseCostFact? lastPurchase, int? costingQuantity, decimal? inventoryValue, decimal averageUnitCost)
    {
        if (lastPurchase is not null)
            return new RestockCostSuggestion(lastPurchase.UnitCost, LastPurchaseSource, lastPurchase.PurchaseDate);

        if (costingQuantity is > 0 && inventoryValue.HasValue && averageUnitCost >= 0)
            return new RestockCostSuggestion(averageUnitCost, AverageUnitCostSource, null);

        return new RestockCostSuggestion(null, NoneSource, null);
    }
}
