using Inventory.Domain.InventoryCounting;

namespace Inventory.Application.InventoryCounting;

/// <summary>
/// The write transaction one Take Inventory apply runs in (issue #1241). Disposing it without
/// <see cref="CommitAsync"/> rolls back everything written through the connection it shares since it
/// began - the count's movement and the quantities and costs its rebuild touched.
///
/// <para>On SQLite it is a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is taken when the
/// transaction opens rather than at its first write. That is what makes the read inside it
/// authoritative: the quantity the count is compared with and the quantity its difference is applied
/// to have to be the same one, and a deferred transaction would let another writer commit between
/// them - stock 10, counted 8, a refill of 4 committed in the gap, and the stale -2 applied to 14.
/// It is the same shape and the same reason as
/// <c>IPickListRefillStore.BeginWriteTransactionAsync</c> (issue #743) and
/// <c>IMachineStockEventStore.BeginRefillWriteTransactionAsync</c> (issue #1127).</para>
/// </summary>
public interface IInventoryCountWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the Take Inventory apply use case (issue #245): reading the
/// authoritative current quantity at the mutation boundary, resolving the same restock-cost
/// suggestion an operator-entered positive Restock already uses, and applying a non-zero movement
/// through the existing costing/audit machinery.
/// </summary>
public interface IInventoryCountAdjustmentStore
{
    /// <summary>
    /// Opens the write transaction the whole apply runs in (issue #1241). The read that decides the
    /// write - the authoritative current quantity - must happen inside it.
    /// </summary>
    Task<IInventoryCountWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken);

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
    ///
    /// <para>It saves inside the caller's transaction and deliberately does <b>not</b> commit: the
    /// use case opened that transaction before it read the quantity this difference was decided from
    /// and owns the commit, so a refusal after this point discards the movement.</para>
    /// </summary>
    Task<InventoryCountAdjustmentApplication> ApplyAsync(
        long productId,
        InventoryCountMovementKind kind,
        int quantityChange,
        decimal? unitCost,
        CancellationToken cancellationToken);
}
