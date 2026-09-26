namespace Inventory.Application.Suppliers;

public sealed class CreateSupplier
{
    private readonly ISupplierStore _store;

    public CreateSupplier(ISupplierStore store)
    {
        _store = store;
    }

    public Task<SupplierRecord> Handle(string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken) =>
        _store.AddAsync(name, contactName, phone, email, address, cancellationToken);
}
