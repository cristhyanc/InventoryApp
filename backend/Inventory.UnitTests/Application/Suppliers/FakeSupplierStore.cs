#nullable enable

using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;

namespace InventoryApi.Tests.Application.Suppliers;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
/// </summary>
public sealed class FakeSupplierStore : ISupplierStore
{
    private readonly List<SupplierRecord> _suppliers = [];
    private int _nextId = 1;

    /// <summary>The GST defaults stored per supplier, absent until something sets them (issue #430).</summary>
    private readonly Dictionary<int, SupplierGstDefaults> _gstDefaults = [];

    public Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierRecord>>(
            _suppliers.OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

    public Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_suppliers.SingleOrDefault(x => x.Id == id));

    public Task<SupplierRecord> AddAsync(SupplierFields fields, CancellationToken cancellationToken)
    {
        var record = new SupplierRecord(
            _nextId++, fields.Name, fields.ContactName, fields.Phone, fields.Email, fields.Address, fields.Url);
        _suppliers.Add(record);
        return Task.FromResult(record);
    }

    public Task<SupplierRecord?> UpdateAsync(int id, SupplierFields fields, CancellationToken cancellationToken)
    {
        var existing = _suppliers.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult<SupplierRecord?>(null);

        var updated = existing with
        {
            Name = fields.Name,
            ContactName = fields.ContactName,
            Phone = fields.Phone,
            Email = fields.Email,
            Address = fields.Address,
            Url = fields.Url,
        };
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

    public Task<SupplierGstDefaults?> FindGstDefaultsAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult<SupplierGstDefaults?>(
            _suppliers.Any(x => x.Id == id)
                ? _gstDefaults.TryGetValue(id, out var defaults) ? defaults : SupplierGstDefaults.None
                : null);

    public Task<bool> SetGstDefaultsAsync(int id, SupplierGstDefaults defaults, CancellationToken cancellationToken)
    {
        if (!_suppliers.Any(x => x.Id == id)) return Task.FromResult(false);

        _gstDefaults[id] = defaults;
        return Task.FromResult(true);
    }
}
