using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// The manual stock-adjustment use case (issue #282), mirroring the former
/// <c>InventoryApi.Services.StockService.Adjust</c>: an unknown product returns <c>null</c> before
/// any validation, then <see cref="ManualStockAdjustmentPolicy"/> validates the request before the
/// movement is applied.
/// </summary>
public sealed class AdjustStock
{
    private readonly IStockAdjustmentStore _store;

    public AdjustStock(IStockAdjustmentStore store) => _store = store;

    public async Task<StockAdjustmentRecord?> Handle(
        long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken)
    {
        if (!await _store.ProductExistsAsync(productId, cancellationToken)) return null;

        ManualStockAdjustmentPolicy.Validate(input.Reason, input.QuantityChange, input.UnitCost);

        return await _store.ApplyAsync(productId, input, cancellationToken);
    }
}
