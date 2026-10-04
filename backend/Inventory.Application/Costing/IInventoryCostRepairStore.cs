namespace Inventory.Application.Costing;

/// <summary>
/// The database transaction a costing-repair apply runs in. Disposing it without
/// <see cref="CommitAsync"/> rolls back every change saved through the store since it began,
/// including what the product cost rebuild staged.
/// </summary>
public interface IInventoryCostRepairTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the costing-repair use cases (issue #359), owned by the Application
/// layer. Its implementation reads and writes through the caller's business scope and applies no
/// repair rule of its own, and it shares its unit of work with the <see cref="IRebuildProductCost"/>
/// rebuild, so one transaction covers appending the repair and recosting the product.
///
/// The port is deliberately append-only: there is no method to update or delete a repair, because
/// there is no supported way to do either. The replay inputs themselves come from
/// <see cref="IInventoryCostLedgerStore"/>, so a preview, an apply and a rebuild all read exactly
/// the same ledger.
/// </summary>
public interface IInventoryCostRepairStore
{
    /// <summary>Begins the transaction an apply runs in.</summary>
    Task<IInventoryCostRepairTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>The product, or <c>null</c> when it does not exist (or is not the caller's).</summary>
    Task<InventoryCostRepairProduct?> GetProductAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the repair and persists it inside the caller's transaction, returning the stored
    /// record with its key so the rest of the apply - and the rebuild that follows - sees it.
    /// </summary>
    Task<InventoryCostRepairRecord> AppendAsync(NewInventoryCostRepair repair, CancellationToken cancellationToken);

    /// <summary>The product's repair history, newest effective repair first.</summary>
    Task<IReadOnlyList<InventoryCostRepairRecord>> ListAsync(long productId, CancellationToken cancellationToken);

    /// <summary>Persists every staged change, including a rebuild's.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
