namespace Inventory.Application.SupplierOrders;

/// <summary>Narrow persistence port for supplier-order create/read/cancel orchestration, owned by the Application layer.</summary>
public interface ISupplierOrderStore
{
    /// <summary>
    /// Active orders: still open, oldest expected/order date first. Cancelled, fully received and
    /// explicitly completed-with-undelivered orders (issue #974) are all excluded, because none of
    /// them is waiting for anything.
    /// </summary>
    Task<IReadOnlyList<SupplierOrderRecord>> ListActiveAsync(CancellationToken cancellationToken);

    Task<SupplierOrderRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken);

    Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    Task<SupplierOrderRecord> CreateAsync(SupplierOrderCreateFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the order. Returns <c>false</c> when no order with <paramref name="id"/> exists or
    /// it is already cancelled, fully received, or completed with undelivered quantities (issue
    /// #974) - an order that is already closed is not cancellable, and a completion is never
    /// relabelled as a cancellation.
    /// </summary>
    Task<bool> CancelAsync(int id, CancellationToken cancellationToken);
}
