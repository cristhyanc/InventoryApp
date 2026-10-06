using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISupplierStore"/>. It lives in Inventory.Infrastructure
/// beside the <see cref="AppDbContext"/> and the <see cref="Supplier"/> entity it reads, which moved
/// there in issue #307; this adapter followed them in issue #309 (Persistence 8/8 of #153).
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

    public async Task<SupplierGstDefaults?> FindGstDefaultsAsync(int id, CancellationToken cancellationToken)
    {
        var row = await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == id)
            .Select(supplier => new
            {
                supplier.ProductLineGstDefault,
                supplier.DeliveryGstDefault,
                supplier.PackageGstDefault,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new SupplierGstDefaults(row.ProductLineGstDefault, row.DeliveryGstDefault, row.PackageGstDefault);
    }

    /// <summary>Writes the three default columns only; no other supplier field is touched.</summary>
    public async Task<bool> SetGstDefaultsAsync(
        int id, SupplierGstDefaults defaults, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return false;

        entity.ProductLineGstDefault = defaults.ProductLines;
        entity.DeliveryGstDefault = defaults.Delivery;
        entity.PackageGstDefault = defaults.Package;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static SupplierRecord ToRecord(Supplier entity) =>
        new(entity.Id, entity.Name, entity.ContactName, entity.Phone, entity.Email, entity.Address);
}
