namespace Inventory.Application.Costing;

/// <summary>
/// The authoritative product-cost-rebuild use case's contract (issue #296), replacing the former
/// <c>InventoryApi.Services.Interfaces.IInventoryCostRebuildService</c>, so the purchase, product,
/// stock, Take Inventory, machine-stock and sales-sync adapters, the sale-costing and
/// inventory-cost transition use cases, and the not-yet-migrated import service depend on it rather
/// than on a concrete class.
/// </summary>
public interface IRebuildProductCost
{
    /// <summary>
    /// Replays the product's cost history and, unless <paramref name="dryRun"/>, stages the rebuilt
    /// movement and product positions without saving. Completed sales authorised at or after
    /// <paramref name="recostCompletedSalesFrom"/> are recosted from the inventory ledger.
    /// </summary>
    /// <exception cref="InvalidOperationException">The product does not exist.</exception>
    /// <exception cref="InventoryCostDataQualityException">Not a dry run and the history has a fatal data-quality issue.</exception>
    Task<InventoryCostRebuildResult> RebuildAsync(
        long productId,
        DateTime? recostCompletedSalesFrom = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The weighted-average unit cost immediately before the given sale (or the average at
    /// <paramref name="saleTime"/> when that sale is not reached); <c>null</c> when the product
    /// does not exist or its history up to then has a fatal data-quality issue.
    /// </summary>
    Task<decimal?> GetAverageUnitCostAtAsync(
        long productId,
        DateTime saleTime,
        long? saleTransactionId = null,
        CancellationToken cancellationToken = default);
}
