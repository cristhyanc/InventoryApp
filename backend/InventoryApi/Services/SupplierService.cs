using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class SupplierService : ISupplierService
{
    private readonly AppDbContext _db;
    public SupplierService(AppDbContext db) => _db = db;

    public async Task<IEnumerable<Supplier>> GetAll() => await _db.Suppliers.OrderBy(s => s.Name).ToListAsync();

    public async Task<Supplier?> Get(int id) => await _db.Suppliers.FindAsync(id);

    public async Task<Supplier> Create(string name, string? contactName, string? phone, string? email, string? address)
    {
        var supplier = new Supplier
        {
            Name = name,
            ContactName = contactName,
            Phone = phone,
            Email = email,
            Address = address
        };
        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync();
        return supplier;
    }

    public async Task<bool> Update(int id, string name, string? contactName, string? phone, string? email, string? address)
    {
        var supplier = await _db.Suppliers.FindAsync(id);
        if (supplier is null) return false;
        supplier.Name = name;
        supplier.ContactName = contactName;
        supplier.Phone = phone;
        supplier.Email = email;
        supplier.Address = address;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> Delete(int id)
    {
        var supplier = await _db.Suppliers.FindAsync(id);
        if (supplier is null) return false;
        _db.Suppliers.Remove(supplier);
        await _db.SaveChangesAsync();
        return true;
    }
}
