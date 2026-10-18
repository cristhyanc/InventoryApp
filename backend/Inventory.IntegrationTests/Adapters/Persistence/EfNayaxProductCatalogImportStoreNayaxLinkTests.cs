using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational coverage for the identity the Nayax catalogue import resolves (issue #745, parent
/// #329 decision A2b): a snapshot entry names a local product or category through the provider id
/// recorded on that row - <c>Product.ExternalProductId</c>/<c>Category.ExternalCategoryId</c> since
/// issue #759 - and a product's Nayax <c>ProductGroupID</c> becomes a local <c>CategoryId</c> the
/// same way.
///
/// Every fixture here records a Nayax id on a local row with a <strong>different</strong> key, which
/// is the only shape that can tell the recorded id apart from the coincidence it replaces -
/// <c>Product.Id</c>/<c>Category.Id</c> still being the Nayax id until #735 (A4). These are
/// deliberately written before that divergence exists, so the resolution is pinned while the
/// migrated ids are all id-to-id, exactly as the issue asks.
///
/// SQLite, not an in-memory provider: the insert/update split, the primary keys, the filtered unique
/// indexes over the external id columns and the <c>Products.CategoryId</c> foreign key are all part
/// of what is asserted.
/// </summary>
public class EfNayaxProductCatalogImportStoreNayaxLinkTests : IDisposable
{
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 2, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfNayaxProductCatalogImportStoreNayaxLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(NayaxBusiness(1, "Vending A"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Both businesses in these fixtures sell through Nayax (issue #759), which is what makes the
    /// recorded external ids Nayax ids.
    /// </summary>
    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// The acceptance criterion for category identity: the group's products land on the local
    /// category that carries the group id, no second category is created, and a local category whose
    /// own id happens to equal the group id is neither used nor updated.
    /// </summary>
    [Fact]
    public async Task A_products_group_resolves_to_the_local_category_that_carries_the_group_id()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Categories.AddRange(
                new Category
                {
                    Id = 55,
                    ExternalCategoryId = NayaxExternalId.From(10),
                    Name = "Snacks",
                    Description = "The category the group id names",
                },
                new Category { Id = 10, Name = "Unrelated", Description = "Shares the group's number only" });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal(55, product.CategoryId);

        // No third category: the group id already named one, so nothing was created for it.
        Assert.Equal(
            new long[] { 10, 55 },
            await verify.Categories.OrderBy(category => category.Id).Select(category => category.Id).ToArrayAsync());
        var unrelated = await verify.Categories.SingleAsync(category => category.Id == 10);
        Assert.Equal("Unrelated", unrelated.Name);
        Assert.Equal("Shares the group's number only", unrelated.Description);
        Assert.Null(unrelated.ExternalCategoryId);
        var named = await verify.Categories.SingleAsync(category => category.Id == 55);
        Assert.Equal("Snacks", named.Name);
        Assert.Equal("The category the group id names", named.Description);
    }

    /// <summary>
    /// The same rule for the product itself: the snapshot entry updates the product that carries its
    /// Nayax id, and the local product whose key equals the remote one is left exactly as it is.
    /// </summary>
    [Fact]
    public async Task A_snapshot_entry_updates_the_product_that_carries_its_nayax_id()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Products.AddRange(
                new Product
                {
                    Id = 5001,
                    ExternalProductId = NayaxExternalId.From(100),
                    Name = "Old name",
                    UnitPrice = 1m,
                    QuantityInStock = 7,
                },
                new Product { Id = 100, Name = "Unrelated", UnitPrice = 9m, QuantityInStock = 2 });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var named = await verify.Products.SingleAsync(product => product.Id == 5001);
        Assert.Equal("Chips", named.Name);
        Assert.Equal(3.50m, named.UnitPrice);
        Assert.Equal(ImportedAt, named.UpdatedAt);
        Assert.Equal(7, named.QuantityInStock);

        var unrelated = await verify.Products.SingleAsync(product => product.Id == 100);
        Assert.Equal("Unrelated", unrelated.Name);
        Assert.Equal(9m, unrelated.UnitPrice);
        Assert.NotEqual(ImportedAt, unrelated.UpdatedAt);
        Assert.Null(unrelated.ExternalProductId);

        // Nothing was created, and the recorded id stayed where it was: the Nayax id named a product
        // already.
        Assert.Equal(2, await verify.Products.CountAsync());
        Assert.Equal("100", named.ExternalProductId);
    }

    /// <summary>
    /// A category this snapshot creates is immediately the answer for the products in that group: the
    /// created row's external id joins the lookup in the same unit of work, so a first import still
    /// categorises its products.
    /// </summary>
    [Fact]
    public async Task A_category_created_by_the_same_snapshot_is_resolved_for_its_products()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal(10, product.CategoryId);
        Assert.Equal("10", (await verify.Categories.SingleAsync()).ExternalCategoryId);
    }

    /// <summary>
    /// A group id no local category carries - and that the snapshot's own group list does not carry
    /// either - is an unknown category, so the product is imported uncategorised rather than pointed
    /// at whichever local category happens to hold that number.
    /// </summary>
    [Fact]
    public async Task An_unrecorded_group_id_leaves_the_product_uncategorised()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Categories.Add(new Category { Id = 77, Name = "Unrelated" });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 77)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Null(product.CategoryId);
        Assert.Equal("Unrelated", (await verify.Categories.SingleAsync()).Name);
    }

    /// <summary>
    /// Another business's recorded id resolves nothing here. The lookup is the ordinary
    /// tenant-filtered read and the id is stored on a row that belongs to one business, so the
    /// snapshot creates this business's own product carrying its own copy of the id instead of
    /// reaching across - which is also why the uniqueness rule is per business.
    /// </summary>
    [Fact]
    public async Task Another_businesss_recorded_id_is_not_an_answer_for_this_business()
    {
        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(NayaxBusiness(2, "Vending B"));
            await setup.SaveChangesAsync();
        }

        using (var other = TestAppDbContext.For(_options, 2))
        {
            other.Products.Add(new Product
            {
                Id = 5001,
                ExternalProductId = NayaxExternalId.From(100),
                Name = "Business B product",
            });
            await other.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var created = await verify.Products.SingleAsync();
        Assert.Equal((100L, "Chips", "100"), (created.Id, created.Name, created.ExternalProductId));

        using var verifyOther = TestAppDbContext.For(_options, 2);
        var untouched = await verifyOther.Products.SingleAsync();
        Assert.Equal(("Business B product", "100"), (untouched.Name, untouched.ExternalProductId));
    }

    private async Task Apply(NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, 1);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
