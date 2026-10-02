using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// The already-queried facts <see cref="RestockCostSuggestionPolicy"/> needs to resolve a product's
/// restock-cost suggestion (issue #282): its latest purchase cost (if any) and its current costing
/// facts.
/// </summary>
public sealed record RestockCostFacts(
    LastPurchaseCostFact? LastPurchase, int? CostingQuantity, decimal? InventoryValue, decimal AverageUnitCost);
