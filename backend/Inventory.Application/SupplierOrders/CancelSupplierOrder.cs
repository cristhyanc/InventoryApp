namespace Inventory.Application.SupplierOrders;

public sealed class CancelSupplierOrder
{
    private readonly ISupplierOrderStore _store;

    public CancelSupplierOrder(ISupplierOrderStore store)
    {
        _store = store;
    }

    public Task<bool> Handle(int id, CancellationToken cancellationToken) =>
        _store.CancelAsync(id, cancellationToken);
}
