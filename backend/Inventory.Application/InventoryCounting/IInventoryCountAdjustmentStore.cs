using Inventory.Domain.InventoryCounting;

namespace Inventory.Application.InventoryCounting;

/// <summary>
/// Narrow persistence port for the Take Inventory apply use case (issue #245): reading the
/// authoritative current quantity at the mutation boundary, resolving the same restock-cost
/// suggestion an operator-entered positive Restock already uses, and applying a non-zero movement
/// through the existing costing/audit machinery.
/// </summary>
public interface IInventoryCountAdjustmentStore
{
    /// <summary>Null when the product does not exist (or is not owned by the caller's business).</summary>
    Task<InventoryCountProduct?> GetCurrentStockAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// The same cost suggestion (last purchase cost, else average unit cost) an operator-entered
    /// positive Restock already offers; null when neither is available.
    /// </summary>
    Task<decimal?> GetRestockUnitCostAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a non-zero movement: an increase through the existing positive Restock semantics
    /// (never <c>MachineRefill</c>), a decrease through the existing Correction semantics. Never
    /// called for <see cref="InventoryCountMovementKind.None"/>.
    /// </summary>
    Task<InventoryCountAdjustmentApplication> ApplyAsync(
        long productId,
        InventoryCountMovementKind kind,
        int quantityChange,
        decimal? unitCost,
        CancellationToken cancellationToken);
}
