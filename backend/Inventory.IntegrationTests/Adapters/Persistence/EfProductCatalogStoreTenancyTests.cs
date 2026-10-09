using Inventory.Application.Products;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Cross-tenant regression tests for the migrated product read slice (issue #240): the new
/// <see cref="EfProductCatalogStore"/> reads through the same <c>AppDbContext</c> global query filters
/// every other tenant-owned store uses, so a second business's product - and its category, supplier
/// and stock-adjustment detail - is never visible to a caller scoped to the first business, by listing
/// or by direct id lookup. Relational rather than InMemory, because what is under test is the SQL the
/// filters and include graph actually produce.
/// </summary>
public class EfProductCatalogStoreTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly long _businessBProductId;

    public EfProductCatalogStoreTenancyTests()
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

        setup.Products.Add(new Product { BusinessId = BusinessA, Name = "A-Product", Sku = "SHARED-SKU" });
        var businessBProduct = new Product { BusinessId = BusinessB, Name = "B-Product", Sku = "SHARED-SKU" };
        setup.Products.Add(businessBProduct);
        setup.SaveChanges();
        _businessBProductId = businessBProduct.Id;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ListOrderedByNameAsync_ReturnsOnlyTheCallersBusinessProducts()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfProductCatalogStore(db);

        var products = await store.ListOrderedByNameAsync(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Equal(["A-Product"], products.Select(product => product.Name));
    }

    /// <summary>
    /// A search that matches both businesses' products must still only answer with the caller's: the
    /// filter narrows within the tenant boundary, it never widens past it.
    /// </summary>
    [Fact]
    public async Task ListUnorderedAsync_DoesNotLeakAnotherBusinessProductThroughASharedSearchTerm()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfProductCatalogStore(db);

        var products = await store.ListUnorderedAsync(
            new ProductCatalogFilter("SHARED-SKU", null, null), CancellationToken.None);

        Assert.Equal(["A-Product"], products.Select(product => product.Name));
    }

    [Fact]
    public async Task FindAsync_ReturnsNull_ForAnotherBusinesssProductId()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfProductCatalogStore(db);

        Assert.Null(await store.FindAsync(_businessBProductId, CancellationToken.None));
    }
}
