namespace Inventory.Application.Suppliers;

public sealed class DeleteSupplier
{
    private readonly ISupplierStore _store;

    public DeleteSupplier(ISupplierStore store)
    {
        _store = store;
    }

    public Task<bool> Handle(int id, CancellationToken cancellationToken) =>
        _store.DeleteAsync(id, cancellationToken);
}
