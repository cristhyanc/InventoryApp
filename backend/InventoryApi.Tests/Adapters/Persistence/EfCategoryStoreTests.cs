using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

public class EfCategoryStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    [Fact]
    public async Task ListOrderedByNameAsync_returns_categories_ordered_by_name()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        db.Categories.AddRange(
            new Category { Name = "Zebra" },
            new Category { Name = "Apple", Description = "Fruit" });
        await db.SaveChangesAsync();
        var store = new EfCategoryStore(db);

        var categories = await store.ListOrderedByNameAsync(CancellationToken.None);

        Assert.Equal(new[] { "Apple", "Zebra" }, categories.Select(c => c.Name));
        Assert.Equal("Fruit", categories.Single(c => c.Name == "Apple").Description);
    }

    [Fact]
    public async Task FindByIdAsync_returns_existing_category()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long id;
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var category = new Category { Name = "Snacks" };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            id = category.Id;
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(verify);

        var found = await store.FindByIdAsync(id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Snacks", found!.Name);
    }

    [Fact]
    public async Task FindByIdAsync_returns_null_for_missing_category()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        var found = await store.FindByIdAsync(999, CancellationToken.None);

        Assert.Null(found);
    }

    /// <summary>
    /// Category name has no uniqueness constraint today (AppDbContext indexes but does not
    /// enforce uniqueness on Category.Name) - this locks in that existing, deliberately
    /// permissive behavior against the real relational schema.
    /// </summary>
    [Fact]
    public async Task Two_categories_with_the_same_name_are_both_persisted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        db.Categories.AddRange(
            new Category { Name = "Same Name" },
            new Category { Name = "Same Name" });
        await db.SaveChangesAsync();
        var store = new EfCategoryStore(db);

        var categories = await store.ListOrderedByNameAsync(CancellationToken.None);

        Assert.Equal(2, categories.Count);
    }

    /// <summary>
    /// The central tenant filter (issue #64), exercised through this adapter: a category owned
    /// by another business is invisible to both the list and by-id queries.
    /// </summary>
    [Fact]
    public async Task Another_business_category_is_invisible_to_list_and_find()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long otherBusinessCategoryId;
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            var category = new Category { Name = "B Category" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            otherBusinessCategoryId = category.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        Assert.Empty(await store.ListOrderedByNameAsync(CancellationToken.None));
        Assert.Null(await store.FindByIdAsync(otherBusinessCategoryId, CancellationToken.None));
    }
}
