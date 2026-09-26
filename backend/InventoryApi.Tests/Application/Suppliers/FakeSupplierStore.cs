#nullable enable

using Inventory.Application.Suppliers;

namespace InventoryApi.Tests.Application.Suppliers;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
/// </summary>
public sealed class FakeSupplierStore : ISupplierStore
{
    private readonly List<SupplierRecord> _suppliers = [];
    private int _nextId = 1;

    public Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierRecord>>(
            _suppliers.OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

    public Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_suppliers.SingleOrDefault(x => x.Id == id));

    public Task<SupplierRecord> AddAsync(string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken)
    {
        var record = new SupplierRecord(_nextId++, name, contactName, phone, email, address);
        _suppliers.Add(record);
        return Task.FromResult(record);
    }

    public Task<SupplierRecord?> UpdateAsync(int id, string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken)
    {
        var existing = _suppliers.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult<SupplierRecord?>(null);

        var updated = existing with { Name = name, ContactName = contactName, Phone = phone, Email = email, Address = address };
        _suppliers[_suppliers.IndexOf(existing)] = updated;
        return Task.FromResult<SupplierRecord?>(updated);
    }

    public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = _suppliers.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult(false);

        _suppliers.Remove(existing);
        return Task.FromResult(true);
    }
}
