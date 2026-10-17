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
/// #329 decision A2b): a snapshot entry names a local product or category through this business's
/// <c>ProductExternalIds</c>/<c>CategoryExternalIds</c> link, and a product's Nayax
/// <c>ProductGroupID</c> becomes a local <c>CategoryId</c> the same way.
///
/// Every fixture here links a Nayax id to a local id with a <strong>different</strong> value, which
/// is the only shape that can tell the link lookup apart from the coincidence it replaces -
/// <c>Product.Id</c>/<c>Category.Id</c> still being the Nayax id until #735 (A4). These are
/// deliberately written before that divergence exists, so the resolution is pinned while the
/// backfilled links are all id-to-id, exactly as the issue asks.
///
/// SQLite, not an in-memory provider: the insert/update split, the primary keys, the link tables'
/// unique indexes and the <c>Products.CategoryId</c> foreign key are all part of what is asserted.
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
        setup.Businesses.Add(
            new Business { Id = 1, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The acceptance criterion for category identity: the group's products land on the linked local
    /// category, no second category is created, and a local category whose own id happens to equal
    /// the group id is neither used nor updated.
    /// </summary>
    [Fact]
    public async Task A_products_group_resolves_to_the_linked_local_category()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Categories.AddRange(
                new Category { Id = 55, Name = "Snacks", Description = "The linked category" },
                new Category { Id = 10, Name = "Unrelated", Description = "Shares the group's number only" });
            await seed.SaveChangesAsync();
            seed.CategoryExternalIds.Add(CategoryLink(nayaxGroupId: 10, localCategoryId: 55));
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal(55, product.CategoryId);

        // No third category: the group was already linked, so nothing was created for it.
        Assert.Equal(
            new long[] { 10, 55 },
            await verify.Categories.OrderBy(category => category.Id).Select(category => category.Id).ToArrayAsync());
        var unrelated = await verify.Categories.SingleAsync(category => category.Id == 10);
        Assert.Equal("Unrelated", unrelated.Name);
        Assert.Equal("Shares the group's number only", unrelated.Description);
        var linked = await verify.Categories.SingleAsync(category => category.Id == 55);
        Assert.Equal("Snacks", linked.Name);
        Assert.Equal("The linked category", linked.Description);
    }

    /// <summary>
    /// The same rule for the product itself: the snapshot entry updates the product its link names,
    /// and the local product whose id equals the remote one is left exactly as it is.
    /// </summary>
    [Fact]
    public async Task A_snapshot_entry_updates_the_product_its_link_names()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Products.AddRange(
                new Product { Id = 5001, Name = "Old name", UnitPrice = 1m, QuantityInStock = 7 },
                new Product { Id = 100, Name = "Unrelated", UnitPrice = 9m, QuantityInStock = 2 });
            await seed.SaveChangesAsync();
            seed.ProductExternalIds.Add(ProductLink(nayaxProductId: 100, localProductId: 5001));
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var linked = await verify.Products.SingleAsync(product => product.Id == 5001);
        Assert.Equal("Chips", linked.Name);
        Assert.Equal(3.50m, linked.UnitPrice);
        Assert.Equal(ImportedAt, linked.UpdatedAt);
        Assert.Equal(7, linked.QuantityInStock);

        var unrelated = await verify.Products.SingleAsync(product => product.Id == 100);
        Assert.Equal("Unrelated", unrelated.Name);
        Assert.Equal(9m, unrelated.UnitPrice);
        Assert.NotEqual(ImportedAt, unrelated.UpdatedAt);

        // Nothing was created: the Nayax id named a product already, and the unrelated row keeps its
        // own (single) link state.
        Assert.Equal(2, await verify.Products.CountAsync());
        var link = await verify.ProductExternalIds.SingleAsync();
        Assert.Equal(("100", 5001L), (link.ExternalId, link.ProductId));
    }

    /// <summary>
    /// A category this snapshot creates is immediately the answer for the products in that group:
    /// the created row's link joins the lookup in the same unit of work, so a first import still
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
        Assert.Equal("10", (await verify.CategoryExternalIds.SingleAsync()).ExternalId);
    }

    /// <summary>
    /// A group id this business has no link for - and that the snapshot's own group list does not
    /// carry either - is an unknown category, so the product is imported uncategorised rather than
    /// pointed at whichever local category happens to hold that number.
    /// </summary>
    [Fact]
    public async Task An_unlinked_group_id_leaves_the_product_uncategorised()
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
    /// Another business's link resolves nothing here. The lookup is the ordinary tenant-filtered
    /// read, and the link's own composite foreign key keeps it inside its business, so the snapshot
    /// creates this business's own product and link instead of reaching across.
    /// </summary>
    [Fact]
    public async Task Another_businesss_link_is_not_an_answer_for_this_business()
    {
        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(new Business
            {
                Id = 2,
                Name = "Vending B",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        using (var other = TestAppDbContext.For(_options, 2))
        {
            other.Products.Add(new Product { Id = 5001, Name = "Business B product" });
            await other.SaveChangesAsync();
            other.ProductExternalIds.Add(ProductLink(nayaxProductId: 100, localProductId: 5001));
            await other.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var created = await verify.Products.SingleAsync();
        Assert.Equal(100, created.Id);
        Assert.Equal("Chips", created.Name);
        var link = await verify.ProductExternalIds.SingleAsync();
        Assert.Equal((1, "100", 100L), (link.BusinessId, link.ExternalId, link.ProductId));

        using var verifyOther = TestAppDbContext.For(_options, 2);
        Assert.Equal("Business B product", (await verifyOther.Products.SingleAsync()).Name);
    }

    private static ProductExternalId ProductLink(long nayaxProductId, long localProductId) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(nayaxProductId),
            ProductId = localProductId,
        };

    private static CategoryExternalId CategoryLink(long nayaxGroupId, long localCategoryId) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(nayaxGroupId),
            CategoryId = localCategoryId,
        };

    private async Task Apply(NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, 1);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
