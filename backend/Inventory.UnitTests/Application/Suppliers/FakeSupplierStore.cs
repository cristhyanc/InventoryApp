#nullable enable

using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using Inventory.Domain.Suppliers;

namespace InventoryApi.Tests.Application.Suppliers;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
///
/// <para>The write methods run the same <see cref="SupplierPresetLinkPolicy"/> on the same two
/// facts the EF adapter reads (issue #934). That is deliberate rather than a convenience: the
/// policy is the Domain's, both adapters only supply the stored row to it, and a fake that skipped
/// it would let a use-case test pass over behaviour the real store refuses.</para>
/// </summary>
public sealed class FakeSupplierStore : ISupplierStore
{
    private readonly List<SupplierRecord> _suppliers = [];
    private int _nextId = 1;

    /// <summary>The GST defaults stored per supplier, absent until something sets them (issue #430).</summary>
    private readonly Dictionary<int, SupplierGstDefaults> _gstDefaults = [];

    /// <summary>
    /// Records a supplier already linked to a platform preset, as the approval's provisioning copy
    /// creates one (issue #934). There is no use case that links an existing supplier, so this is
    /// test setup rather than a port method.
    /// </summary>
    public SupplierRecord AddLinked(string name, string? url, int supplierPresetId)
    {
        var record = new SupplierRecord(
            _nextId++, name, null, null, null, null, url, supplierPresetId, IsHidden: false);
        _suppliers.Add(record);
        return record;
    }

    public Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierRecord>>(
            _suppliers.OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<SupplierRecord>> ListSelectableOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierRecord>>(
            _suppliers.Where(x => !x.IsHidden).OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

    public Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_suppliers.SingleOrDefault(x => x.Id == id));

    public Task<SupplierRecord> AddAsync(SupplierFields fields, CancellationToken cancellationToken)
    {
        var record = new SupplierRecord(
            _nextId++,
            fields.Name,
            fields.ContactName,
            fields.Phone,
            fields.Email,
            fields.Address,
            fields.Url,
            SupplierPresetId: null,
            IsHidden: false);
        _suppliers.Add(record);
        return Task.FromResult(record);
    }

    public Task<SupplierWriteResult> UpdateAsync(int id, SupplierFields fields, CancellationToken cancellationToken)
    {
        var existing = _suppliers.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult(SupplierWriteResult.NotFound());

        var refusal = SupplierPresetLinkPolicy.RefuseUpdate(
            SupplierPresetLinkPolicy.IsLinked(existing.SupplierPresetId),
            existing.Name,
            existing.Url,
            fields.Name,
            fields.Url);

        if (refusal is not null) return Task.FromResult(SupplierWriteResult.Invalid(refusal));

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
        return Task.FromResult(SupplierWriteResult.Success(updated));
    }

    public Task<SupplierCommandResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var existing = _suppliers.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult(SupplierCommandResult.NotFound);

        var refusal = SupplierPresetLinkPolicy.RefuseDelete(
            SupplierPresetLinkPolicy.IsLinked(existing.SupplierPresetId));

        if (refusal is not null) return Task.FromResult(SupplierCommandResult.Invalid(refusal));

        _suppliers.Remove(existing);
        return Task.FromResult(SupplierCommandResult.Success);
    }

    public Task<SupplierCommandResult> SetHiddenAsync(int id, bool isHidden, CancellationToken cancellationToken)
    {
        var existing = _suppliers.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult(SupplierCommandResult.NotFound);

        var refusal = SupplierPresetLinkPolicy.RefuseHide(
            SupplierPresetLinkPolicy.IsLinked(existing.SupplierPresetId));

        if (refusal is not null) return Task.FromResult(SupplierCommandResult.Invalid(refusal));

        _suppliers[_suppliers.IndexOf(existing)] = existing with { IsHidden = isHidden };
        return Task.FromResult(SupplierCommandResult.Success);
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
