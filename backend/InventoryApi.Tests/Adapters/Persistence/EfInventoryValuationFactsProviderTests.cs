using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite test: proves the projection reads the persisted <c>InventoryValue</c> column,
/// including its nullability, rather than relying on in-memory LINQ semantics that could hide a
/// translation problem.
/// </summary>
public class EfInventoryValuationFactsProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task Returns_each_products_persisted_inventory_value_including_unknown()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke", InventoryValue = 40m },
            new Product { Id = 2, Name = "Chips", InventoryValue = 0m },
            new Product { Id = 3, Name = "Water", InventoryValue = null });
        await db.SaveChangesAsync();
        var provider = new EfInventoryValuationFactsProvider(db);

        var values = await provider.GetProductInventoryValuesAsync(CancellationToken.None);

        Assert.Equal(3, values.Count);
        Assert.Contains(40m, values);
        Assert.Contains(0m, values);
        Assert.Contains(null, values);
    }

    [Fact]
    public async Task No_products_returns_an_empty_list()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfInventoryValuationFactsProvider(db);

        var values = await provider.GetProductInventoryValuesAsync(CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public async Task Business_scoped_context_only_sees_its_own_products()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        setup.Products.AddRange(
            new Product { Id = 1, Name = "Business A product", InventoryValue = 40m, BusinessId = 1 },
            new Product { Id = 2, Name = "Business B product", InventoryValue = 999m, BusinessId = 2 });
        await setup.SaveChangesAsync();

        await using var scopedDb = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        var provider = new EfInventoryValuationFactsProvider(scopedDb);

        var values = await provider.GetProductInventoryValuesAsync(CancellationToken.None);

        Assert.Equal(new decimal?[] { 40m }, values);
    }
}
