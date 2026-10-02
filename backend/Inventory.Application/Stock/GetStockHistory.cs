namespace Inventory.Application.Stock;

/// <summary>
/// The stock-history use case (issue #282), mirroring the former
/// <c>InventoryApi.Services.StockService.History</c>: an unknown product answers an empty history
/// rather than a not-found signal, exactly as before - <c>StockController.History</c> is what turns
/// an empty result into <c>404</c>.
/// </summary>
public sealed class GetStockHistory
{
    private readonly IStockAdjustmentStore _store;

    public GetStockHistory(IStockAdjustmentStore store) => _store = store;

    public async Task<IReadOnlyList<StockAdjustmentRecord>> Handle(long productId, CancellationToken cancellationToken)
    {
        if (!await _store.ProductExistsAsync(productId, cancellationToken))
            return Array.Empty<StockAdjustmentRecord>();

        return await _store.ListHistoryAsync(productId, cancellationToken);
    }
}
