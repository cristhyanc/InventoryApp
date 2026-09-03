using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using Microsoft.AspNetCore.Http;

namespace InventoryApi.Services.Interfaces;

public interface IMachineService
{
    Task<Machine?> GetById(long id);
    Task<List<Machine>> GetAll();
    Task<List<Product>> GetMachineProducts(long id);
    Task<(int Imported, int Updated, int Skipped)> ImportNayaxSalesFromExcelAsync(IFormFile file, CancellationToken ct = default);
}
