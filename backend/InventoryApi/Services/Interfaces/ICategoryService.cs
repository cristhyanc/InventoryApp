using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface ICategoryService
{
    Task<IEnumerable<Category>> GetAll();
    Task<Category?> Get(int id);
    Task<Category> Create(string name, string? description);
    Task<bool> Update(int id, string name, string? description);
    Task<bool> Delete(int id);
}
