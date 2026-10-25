using Inventory.Application.Products;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The catalogue read contract for a retired product (issue #736). <c>IsActive</c> is what a product
/// with history is given instead of a delete, and the issue's criterion is that such a product is
/// hidden from the lists, the pick lists and the reorder alerts while it keeps all of its history and
/// can be reactivated. This is the listing half of that: the working catalogue leaves it out, the
/// whole catalogue is asked for explicitly, and a direct read still answers it.
///
/// <para>Relational SQLite rather than an in-memory provider, because the exclusion is a predicate in
/// the SQL beside the search/category/supplier narrowing and the category/supplier/stock-adjustment
/// include graph, and what is asserted is what that query returns.</para>
/// </summary>
public class EfProductCatalogStoreRetirementTests : IDisposable
{
    private const int BusinessA = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly long _retiredProductId;

    public EfProductCatalogStoreRetirementTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
        });
        setup.SaveChanges();

        var retired = new Product { BusinessId = BusinessA, Name = "Retired", Sku = "SHARED-SKU", IsActive = false };
        setup.Products.AddRange(
            new Product { BusinessId = BusinessA, Name = "Active", Sku = "SHARED-SKU", IsActive = true },
            retired);
        setup.SaveChanges();
        _retiredProductId = retired.Id;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static EfProductCatalogStore StoreFor(AppDbContext db) => new(db);

    private static ProductCatalogFilter WorkingCatalogue(string? search = null) =>
        new(search, null, IncludeRetired: false);

    private static ProductCatalogFilter WholeCatalogue(string? search = null) =>
        new(search, null, IncludeRetired: true);

    [Fact]
    public async Task ListOrderedByNameAsync_LeavesARetiredProductOutOfTheWorkingCatalogue()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var products = await StoreFor(db).ListOrderedByNameAsync(WorkingCatalogue(), CancellationToken.None);

        Assert.Equal(["Active"], products.Select(product => product.Name));
    }

    [Fact]
    public async Task ListOrderedByNameAsync_AnswersARetiredProduct_WhenTheWholeCatalogueIsAskedFor()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var products = await StoreFor(db).ListOrderedByNameAsync(WholeCatalogue(), CancellationToken.None);

        Assert.Equal(["Active", "Retired"], products.Select(product => product.Name));
        Assert.False(products.Single(product => product.Name == "Retired").IsActive);
    }

    [Fact]
    public async Task ListUnorderedAsync_LeavesARetiredProductOutOfTheWorkingCatalogue()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var products = await StoreFor(db).ListUnorderedAsync(
            WorkingCatalogue(), resolveInventoryHistory: false, CancellationToken.None);

        Assert.Equal(["Active"], products.Select(product => product.Name));
    }

    /// <summary>
    /// The exclusion composes with the request's own narrowing rather than replacing it: a search
    /// both products match still answers only the active one.
    /// </summary>
    [Fact]
    public async Task ListUnorderedAsync_AppliesTheRetirementRuleBesideTheRequestsOwnNarrowing()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var working = await StoreFor(db).ListUnorderedAsync(
            WorkingCatalogue("SHARED-SKU"), resolveInventoryHistory: false, CancellationToken.None);
        var whole = await StoreFor(db).ListUnorderedAsync(
            WholeCatalogue("SHARED-SKU"), resolveInventoryHistory: false, CancellationToken.None);

        Assert.Equal(["Active"], working.Select(product => product.Name));
        Assert.Equal(["Active", "Retired"], whole.Select(product => product.Name).Order());
    }

    /// <summary>
    /// <see cref="ProductCatalogFilter.None"/> is the whole catalogue, retired products included,
    /// because its two consumers - the home Dashboard summary and the machine product listing - read
    /// every row and apply their own activity rule to it. Pinned here so narrowing it later has to be
    /// a conscious change: a machine slot or a refill alert that names a retired product would
    /// otherwise silently stop describing it.
    /// </summary>
    [Fact]
    public async Task ProductCatalogFilterNone_StillAnswersEveryProductTheBusinessOwns()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var products = await StoreFor(db).ListUnorderedAsync(
            ProductCatalogFilter.None, resolveInventoryHistory: false, CancellationToken.None);

        Assert.Equal(["Active", "Retired"], products.Select(product => product.Name).Order());
    }

    /// <summary>
    /// The read that keeps a retired product reachable: its history and its reactivation are read by
    /// id, and retirement decides what a listing offers to work with, never what the business may
    /// look at.
    /// </summary>
    [Fact]
    public async Task FindAsync_AnswersARetiredProduct()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var product = await StoreFor(db).FindAsync(_retiredProductId, CancellationToken.None);

        Assert.NotNull(product);
        Assert.Equal("Retired", product.Name);
        Assert.False(product.IsActive);
    }

    /// <summary>
    /// Reactivating is the whole point of the explicit read: a product the working catalogue stopped
    /// listing returns to it as soon as its flag is set back, with nothing else done to it.
    /// </summary>
    [Fact]
    public async Task AReactivatedProductReturnsToTheWorkingCatalogue()
    {
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var product = await db.Products.SingleAsync(candidate => candidate.Id == _retiredProductId);
            product.IsActive = true;
            await db.SaveChangesAsync();
        }

        await using var read = TestAppDbContext.For(_options, BusinessA);

        var products = await StoreFor(read).ListOrderedByNameAsync(WorkingCatalogue(), CancellationToken.None);

        Assert.Equal(["Active", "Retired"], products.Select(product => product.Name));
    }
}
