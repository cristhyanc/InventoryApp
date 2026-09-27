namespace Inventory.Application.Suppliers;

public sealed class GetSupplier
{
    private readonly ISupplierStore _store;

    public GetSupplier(ISupplierStore store)
    {
        _store = store;
    }

    public Task<SupplierRecord?> Handle(int id, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(id, cancellationToken);
}
