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
    /// <paramref name="recostCompletedSalesFrom"/> are recosted from the inventory ledger. A fatal
    /// data-quality issue stages nothing at all before it throws (issue #362), so a caller may catch
    /// it and still save the products it rebuilt successfully.
    /// </summary>
    /// <exception cref="InvalidOperationException">The product does not exist.</exception>
    /// <exception cref="InventoryCostDataQualityException">Not a dry run and the history has a fatal data-quality issue.</exception>
    Task<InventoryCostRebuildResult> RebuildAsync(
        long productId,
        DateTime? recostCompletedSalesFrom = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same replay and the same decide-before-staging rule as <see cref="RebuildAsync"/>, but
    /// staging only what a costing repair has authority over (issue #359): the product's costing
    /// quantity, inventory value and average unit cost, and the ledger cost of the completed sales
    /// authorised at or after <paramref name="recostCompletedSalesFrom"/>. The product's physical
    /// quantity and every stock movement's stored running position and cost are left exactly as
    /// they are, so applying a repair cannot restate physical stock, a <c>StockAdjustment</c> row or
    /// MachineRefill history. The returned physical quantity is still the replayed one, reported and
    /// not persisted. There is no dry run: the repair preview replays the ledger itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The product does not exist.</exception>
    /// <exception cref="InventoryCostDataQualityException">The history has a fatal data-quality issue.</exception>
    Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
        long productId,
        DateTime recostCompletedSalesFrom,
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
