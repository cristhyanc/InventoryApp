namespace Inventory.Application.SupplierOrders;

/// <summary>Narrow persistence port for supplier-order create/read/cancel orchestration, owned by the Application layer.</summary>
public interface ISupplierOrderStore
{
    /// <summary>Active orders: neither cancelled nor fully received, oldest expected/order date first.</summary>
    Task<IReadOnlyList<SupplierOrderRecord>> ListActiveAsync(CancellationToken cancellationToken);

    Task<SupplierOrderRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken);

    Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    Task<SupplierOrderRecord> CreateAsync(SupplierOrderCreateFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the order. Returns <c>false</c> when no order with <paramref name="id"/> exists or
    /// it is already cancelled or fully received.
    /// </summary>
    Task<bool> CancelAsync(int id, CancellationToken cancellationToken);
}
