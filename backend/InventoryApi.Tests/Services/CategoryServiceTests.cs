using System.Linq;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
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
    public async Task GetAll_returns_categories()
    {
        using var db = CreateDbContext("cat_getall");
        db.Categories.AddRange(
            new Category { Id = 1, Name = "Beverages" },
            new Category { Id = 2, Name = "Snacks" });
        await db.SaveChangesAsync();

        var categories = (await new CategoryService(db).GetAll()).ToList();

        Assert.Equal(2, categories.Count);
        Assert.Equal(new[] { "Beverages", "Snacks" }, categories.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Get_returns_existing_category()
    {
        using var db = CreateDbContext("cat_get");
        db.Categories.Add(new Category { Id = 7, Name = "Snacks" });
        await db.SaveChangesAsync();

        var category = await new CategoryService(db).Get(7);

        Assert.NotNull(category);
        Assert.Equal("Snacks", category!.Name);
    }

    [Fact]
    public async Task Get_returns_null_for_missing_category()
    {
        using var db = CreateDbContext("cat_missing");

        var category = await new CategoryService(db).Get(999L);

        Assert.Null(category);
    }
}
