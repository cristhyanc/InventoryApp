using Inventory.Application.Nayax;

namespace Inventory.Application.SalesSync;

/// <summary>
/// The outcome of persisting one coordinated batch of latest Nayax sales.
/// <see cref="EarliestCompletedSaleByProductId"/> maps each local product whose costed inventory was
/// touched by a completed sale in this batch to the earliest such sale's authorization time, which is
/// the point the inventory-cost replay has to restart from. An empty map means nothing completed
/// changed, so no rebuild is required.
/// </summary>
public sealed record LatestNayaxSalesPersistResult(
    IReadOnlyDictionary<long, DateTime> EarliestCompletedSaleByProductId);

/// <summary>
/// Narrow persistence port for the coordinated latest-Nayax-sales synchronization (issue #187),
/// owned by the Application layer. Transaction deduplication by the remote <c>TransactionID</c>,
/// local product matching, completed/cancelled classification, historical sale costing, and the
/// inventory-cost rebuild all stay behind it, because each of them still depends on the persistence
/// model and the legacy costing services in <c>InventoryApi</c>; the use case therefore stays free of
/// EF Core.
/// </summary>
public interface ILatestNayaxSalesStore
{
    /// <summary>
    /// Persists the batch as <c>NayaxSales</c> rows: a transaction not stored yet is added and
    /// costed, one already stored is only enriched where it is still missing its product match or
    /// status, and nothing is ever double counted. The whole batch is saved once, so a Nayax read
    /// that fails part-way through the coordinated sync leaves no partially imported refresh behind.
    /// </summary>
    Task<LatestNayaxSalesPersistResult> PersistLatestSalesAsync(
        IReadOnlyList<NayaxLastSalesReport> sales, CancellationToken cancellationToken);

    /// <summary>
    /// Rebuilds the affected products' inventory costs chronologically from the given earliest
    /// completed-sale instants, for the products whose transition baseline cutoff that instant is
    /// actually after. Every product that can be rebuilt is rebuilt and saved even when another
    /// product's history is unreplayable; a product that fails is left exactly as it was and the
    /// collected failures are then raised together as one
    /// <see cref="Costing.InventoryCostDataQualityException"/>, so the sync's already persisted
    /// sales stay intact and the failure stays visible (issue #362).
    /// </summary>
    /// <exception cref="Costing.InventoryCostDataQualityException">
    /// At least one affected product's cost history has a fatal data-quality issue.
    /// </exception>
    Task RebuildInventoryCostsAsync(
        IReadOnlyDictionary<long, DateTime> earliestCompletedSaleByProductId,
        CancellationToken cancellationToken);
}
