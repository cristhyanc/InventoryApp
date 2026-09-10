using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class CategoryServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetAll_returns_categories_ordered_by_name()
    {
        using var db = CreateDbContext("cat_test");
        db.Categories.AddRange(
            new Category { Id = 1, Name = "Zebra" },
            new Category { Id = 2, Name = "Apple" });
        await db.SaveChangesAsync();
        ICategoryService svc = new CategoryService(db);

        var categories = await svc.GetAll();

        Assert.Collection(categories,
            category => Assert.Equal("Apple", category.Name),
            category => Assert.Equal("Zebra", category.Name));
    }

    [Fact]
    public async Task Get_returns_existing_category()
    {
        using var db = CreateDbContext("cat_get_test");
        db.Categories.Add(new Category { Id = 1, Name = "Snacks" });
        await db.SaveChangesAsync();
        ICategoryService svc = new CategoryService(db);

        var category = await svc.Get(1);

        Assert.NotNull(category);
        Assert.Equal("Snacks", category.Name);
    }

    [Fact]
    public async Task Get_returns_null_for_missing_category()
    {
        using var db = CreateDbContext("cat_missing_test");
        ICategoryService svc = new CategoryService(db);

        var category = await svc.Get(999);

        Assert.Null(category);
    }
}
