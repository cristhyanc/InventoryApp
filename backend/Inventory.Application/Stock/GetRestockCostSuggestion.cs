using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// The one authoritative restock-cost-suggestion path (issue #282): the latest purchase cost, else
/// the product's average unit cost when valid, else none - mirroring the former
/// <c>InventoryApi.Services.StockService.GetRestockCostSuggestion</c>. An operator-entered positive
/// Restock (<c>StockController</c>) and Take Inventory (<c>EfInventoryCountAdjustmentStore</c>, issue
/// #245) both consume this same use case instead of duplicating the rule.
/// </summary>
public sealed class GetRestockCostSuggestion : IGetRestockCostSuggestion
{
    private readonly IStockAdjustmentStore _store;

    public GetRestockCostSuggestion(IStockAdjustmentStore store) => _store = store;

    public async Task<RestockCostSuggestion?> Handle(long productId, CancellationToken cancellationToken)
    {
        var facts = await _store.GetRestockCostFactsAsync(productId, cancellationToken);
        if (facts is null) return null;

        return RestockCostSuggestionPolicy.Resolve(facts.LastPurchase, facts.CostingQuantity, facts.InventoryValue, facts.AverageUnitCost);
    }
}
