using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface ISupplierService
{
    Task<IEnumerable<Supplier>> GetAll();
    Task<Supplier?> Get(int id);
    Task<Supplier> Create(string name, string? contactName, string? phone, string? email, string? address);
    Task<bool> Update(int id, string name, string? contactName, string? phone, string? email, string? address);
    Task<bool> Delete(int id);
}
