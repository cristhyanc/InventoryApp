using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface IMachineService
{
    Task<Machine?> GetById(long id);
    Task<List<Machine>> GetAll();
    Task<List<Product>> GetMachineProducts(long id);
}
