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

    public async Task<Category?> Get(int id) => await _db.Categories.FindAsync(id);

    public async Task<Category> Create(string name, string? description)
    {
        var category = new Category { Name = name, Description = description };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        return category;
    }

    public async Task<bool> Update(int id, string name, string? description)
    {
        var category = await _db.Categories.FindAsync(id);
        if (category is null) return false;
        category.Name = name;
        category.Description = description;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> Delete(int id)
    {
        var category = await _db.Categories.FindAsync(id);
        if (category is null) return false;
        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();
        return true;
    }
}
