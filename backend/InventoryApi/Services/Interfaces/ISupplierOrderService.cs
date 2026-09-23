using InventoryApi.DTOs;
using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface ISupplierOrderService
{
    Task<IEnumerable<SupplierOrder>> GetActive();
    Task<SupplierOrder?> GetById(int id);
    Task<SupplierOrder?> Create(SupplierOrderCreateDto dto);
    Task<bool> Cancel(int id);
}
