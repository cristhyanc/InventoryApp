namespace Inventory.Application.Suppliers;

public sealed class UpdateSupplier
{
    private readonly ISupplierStore _store;

    public UpdateSupplier(ISupplierStore store)
    {
        _store = store;
    }

    public async Task<bool> Handle(int id, string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken) =>
        await _store.UpdateAsync(id, name, contactName, phone, email, address, cancellationToken) is not null;
}
