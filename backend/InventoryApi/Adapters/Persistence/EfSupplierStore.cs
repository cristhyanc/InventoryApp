using Inventory.Application.Suppliers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ISupplierStore"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and
/// <see cref="Supplier"/> moved there in issue #307, and moving this adapter family after them is
/// Persistence 7/8 and 8/8 of #153.
/// </summary>
public sealed class EfSupplierStore : ISupplierStore
{
    private readonly AppDbContext _db;

    public EfSupplierStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        await _db.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => ToRecord(s))
            .ToListAsync(cancellationToken);

    public async Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<SupplierRecord> AddAsync(string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken)
    {
        var entity = new Supplier { Name = name, ContactName = contactName, Phone = phone, Email = email, Address = address };
        _db.Suppliers.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<SupplierRecord?> UpdateAsync(int id, string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return null;

        entity.Name = name;
        entity.ContactName = contactName;
        entity.Phone = phone;
        entity.Email = email;
        entity.Address = address;
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return false;

        _db.Suppliers.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static SupplierRecord ToRecord(Supplier entity) =>
        new(entity.Id, entity.Name, entity.ContactName, entity.Phone, entity.Email, entity.Address);
}
