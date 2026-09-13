using InventoryApi.DTOs;
using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface ISupplierOrderService
{
    Task<IEnumerable<SupplierOrder>> GetActive();
    Task<SupplierOrder?> Create(SupplierOrderCreateDto dto);
    Task<bool> Cancel(int id);
}