namespace Inventory.Application.Suppliers;

public sealed class ListSuppliers
{
    private readonly ISupplierStore _store;

    public ListSuppliers(ISupplierStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<SupplierRecord>> Handle(CancellationToken cancellationToken) =>
        _store.ListOrderedByNameAsync(cancellationToken);
}
