using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface ICategoryService
{
    Task<IEnumerable<Category>> GetAll();
    Task<Category?> Get(int id);
}
