namespace Inventory.Application.SupplierOrders;

public sealed class GetSupplierOrder
{
    private readonly ISupplierOrderStore _store;

    public GetSupplierOrder(ISupplierOrderStore store)
    {
        _store = store;
    }

    public Task<SupplierOrderRecord?> Handle(int id, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(id, cancellationToken);
}
