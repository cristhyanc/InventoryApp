namespace Inventory.Application.SupplierOrders;

public sealed class ListActiveSupplierOrders
{
    private readonly ISupplierOrderStore _store;

    public ListActiveSupplierOrders(ISupplierOrderStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<SupplierOrderRecord>> Handle(CancellationToken cancellationToken) =>
        _store.ListActiveAsync(cancellationToken);
}
