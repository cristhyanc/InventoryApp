using InventoryApi.DTOs;
using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface IProductService
{
    Task<IEnumerable<Product>> GetAll(string? search, long? categoryId, int? supplierId, bool? lowStockOnly);
    Task<Product?> Get(long id);
    Task<IEnumerable<Product>> LowStock(string? search = null, long? categoryId = null, int? supplierId = null);
    Task<Product> Create(ProductCreateDto dto);
    Task<bool> Update(long id, ProductUpdateDto dto);
    Task<bool> Delete(long id);
}
