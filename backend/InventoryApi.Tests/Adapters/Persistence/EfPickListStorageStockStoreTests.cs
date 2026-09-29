using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="EfPickListStorageStockStore"/> (issue #221) is the Pick List projection's only
/// touchpoint with persisted storage quantity; it must return the same tenant-scoped read every other
/// <c>AppDbContext</c> query gets, with no per-call business filter of its own (issue #64).
/// </summary>
public class EfPickListStorageStockStoreTests
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
    public async Task GetStorageProductsAsync_ReturnsNameAndQuantityInStock_KeyedByProductId()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Coke Zero", QuantityInStock = 42 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfPickListStorageStockStore(db);

        var result = await store.GetStorageProductsAsync([100], CancellationToken.None);

        var product = Assert.Single(result.Values);
        Assert.Equal(100, product.ProductId);
        Assert.Equal("Coke Zero", product.ProductName);
        Assert.Equal(42, product.QuantityInStock);
    }

    [Fact]
    public async Task GetStorageProductsAsync_OnlyReturnsTheRequestedIds()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Coke Zero", QuantityInStock = 42 });
            seed.Products.Add(new Product { Id = 200, Name = "Sprite", QuantityInStock = 7 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfPickListStorageStockStore(db);

        var result = await store.GetStorageProductsAsync([100], CancellationToken.None);

        Assert.Single(result);
        Assert.True(result.ContainsKey(100));
    }

    /// <summary>
    /// The central tenant filter (issue #64), exercised through this adapter: a product owned by
    /// another business - even one whose ID this business's own Nayax mapping happens to reference -
    /// never appears in the result, so the Pick List use case cannot fabricate its storage quantity.
    /// </summary>
    [Fact]
    public async Task GetStorageProductsAsync_ExcludesAnotherBusinesssProduct()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Business B Product", QuantityInStock = 99 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfPickListStorageStockStore(db);

        var result = await store.GetStorageProductsAsync([100], CancellationToken.None);

        Assert.Empty(result);
    }
}
