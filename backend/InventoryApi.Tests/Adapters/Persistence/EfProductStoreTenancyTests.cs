using Inventory.Application.Products;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Cross-tenant regression test for the migrated product create/update/delete slice (issue #240):
/// the new <see cref="EfProductStore"/> reads and writes through the same <c>AppDbContext</c> global
/// query filters and <c>BusinessOwnershipEnforcer</c> every other tenant-owned store uses, so a
/// second business's product is never visible to, or mutable by, a caller scoped to the first
/// business.
/// </summary>
public class EfProductStoreTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfProductStoreTenancyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ProductUpdateFields UpdateFields() => new("SKU-B", null, 5, 10, "unit", null, true);

    [Fact]
    public async Task ExistsAsync_IsFalse_ForAnotherBusinesssProduct()
    {
        long otherBusinessProductId;
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            var product = new Product { BusinessId = BusinessB, Name = "B-Product" };
            seed.Products.Add(product);
            await seed.SaveChangesAsync();
            otherBusinessProductId = product.Id;
        }

        using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfProductStore(db, new InventoryCostRebuildService(db));

        Assert.False(await store.ExistsAsync(otherBusinessProductId, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_DoesNotChange_AnotherBusinesssProduct()
    {
        long otherBusinessProductId;
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            var product = new Product { BusinessId = BusinessB, Name = "B-Product", Sku = "ORIGINAL" };
            seed.Products.Add(product);
            await seed.SaveChangesAsync();
            otherBusinessProductId = product.Id;
        }

        bool updated;
        using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = new EfProductStore(db, new InventoryCostRebuildService(db));
            updated = await store.UpdateAsync(otherBusinessProductId, UpdateFields(), CancellationToken.None);
        }

        // A cross-tenant target is reported as not updated, so the caller answers not-found rather
        // than a success that changed nothing.
        Assert.False(updated);

        using var verify = TestAppDbContext.Unrestricted(_options);
        var untouched = await verify.Products.SingleAsync(product => product.Id == otherBusinessProductId);
        Assert.Equal("ORIGINAL", untouched.Sku);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_AndDoesNotDelete_AnotherBusinesssProduct()
    {
        long otherBusinessProductId;
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            var product = new Product { BusinessId = BusinessB, Name = "B-Product" };
            seed.Products.Add(product);
            await seed.SaveChangesAsync();
            otherBusinessProductId = product.Id;
        }

        bool deleted;
        using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = new EfProductStore(db, new InventoryCostRebuildService(db));
            deleted = await store.DeleteAsync(otherBusinessProductId, CancellationToken.None);
        }

        Assert.False(deleted);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.True(await verify.Products.AnyAsync(product => product.Id == otherBusinessProductId));
    }
}
