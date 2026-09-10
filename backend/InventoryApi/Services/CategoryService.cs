using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class CategoryService : ICategoryService
{
    private readonly AppDbContext _db;
    public CategoryService(AppDbContext db) => _db = db;

    public async Task<IEnumerable<Category>> GetAll() => await _db.Categories.OrderBy(c => c.Name).ToListAsync();

    public async Task<Category?> Get(int id) => await _db.Categories.FindAsync((long)id);
}
