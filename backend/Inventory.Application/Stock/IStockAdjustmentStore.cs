using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// Narrow persistence port for stock history, restock-cost-suggestion facts, and manual
/// stock-adjustment orchestration (issue #282), owned by the Application layer.
/// </summary>
public interface IStockAdjustmentStore
{
    Task<bool> ProductExistsAsync(long productId, CancellationToken cancellationToken);

    /// <summary>Every persisted movement for the product, most recent first. Empty when none exist.</summary>
    Task<IReadOnlyList<StockAdjustmentRecord>> ListHistoryAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// One bounded page of the movement history across products, most recent first, narrowed by
    /// <paramref name="filter"/> (issue #384). The total is the count of movements the filter
    /// matches, not the number returned. The filter is already resolved - UTC boundaries and a
    /// skip/take - so an implementation applies it as given and decides no window, bound or
    /// ordering instant of its own.
    /// </summary>
    Task<StockHistoryResult> QueryHistoryAsync(StockHistoryFilter filter, CancellationToken cancellationToken);

    /// <summary><c>null</c> when the product does not exist (or is not owned by the caller's business).</summary>
    Task<RestockCostFacts?> GetRestockCostFactsAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a manual stock movement through the existing costing/rebuild machinery and records it
    /// as an auditable <c>StockAdjustment</c> with <see cref="StockAdjustmentSource.Manual"/>, as one
    /// transaction. The caller has already validated the product exists and the movement through
    /// <see cref="ManualStockAdjustmentPolicy"/>.
    /// </summary>
    Task<StockAdjustmentRecord> ApplyAsync(
        long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken);
}
