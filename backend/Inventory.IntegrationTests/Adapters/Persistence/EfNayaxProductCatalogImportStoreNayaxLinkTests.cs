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
/// is the only shape that can tell the recorded id apart from the coincidence it replaced -
/// <c>Product.Id</c>/<c>Category.Id</c> having been the Nayax id until #735 (A4) made a created
/// row's key the database's own. The resolution these tests pin is unchanged by that: what changed
/// is that a row the import creates now has a key of its own, which the two creating cases below
/// assert explicitly.
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
    /// created row joins the lookup in the same unit of work, so a first import still categorises its
    /// products.
    ///
    /// <para>And since issue #735 (A4) it does so without the import knowing the key: the created
    /// category's <c>Category.Id</c> is the database's own and differs from the
    /// <c>ProductGroupID</c>, the product's <c>CategoryId</c> is that generated key, and a re-import
    /// finds the same row again through <c>ExternalCategoryId</c> rather than creating a second one.
    /// This is the acceptance criterion for category identity, so it asserts the generated key
    /// explicitly rather than comparing two numbers that used to be equal.</para>
    /// </summary>
    [Fact]
    public async Task A_category_created_by_the_same_snapshot_gets_a_generated_id_and_is_resolved_for_its_products()
    {
        var import = new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt);

        await Apply(import);

        long createdCategoryId;
        using (var verify = TestAppDbContext.For(_options, 1))
        {
            var category = await verify.Categories.SingleAsync();
            createdCategoryId = category.Id;
            Assert.NotEqual(10, createdCategoryId);
            Assert.Equal("10", category.ExternalCategoryId);
            Assert.Equal(createdCategoryId, (await verify.Products.SingleAsync()).CategoryId);
        }

        // The same snapshot again: the group resolves to the category it created, so no second
        // category appears and the product keeps the generated key it was given.
        await Apply(import);

        using var reverify = TestAppDbContext.For(_options, 1);
        Assert.Equal(createdCategoryId, (await reverify.Categories.SingleAsync()).Id);
        Assert.Equal(createdCategoryId, (await reverify.Products.SingleAsync()).CategoryId);
    }

    /// <summary>
    /// An <strong>existing</strong> product moved into a category the same snapshot creates. This is
    /// the one case where the category's key does not exist when the product is updated (issue
    /// #735), so the product is pointed at the category row and EF writes the assigned key into its
    /// foreign key - and the product here is a tracked, already-stored row rather than one this
    /// import is inserting.
    /// </summary>
    [Fact]
    public async Task An_existing_product_is_moved_into_a_category_the_same_snapshot_creates()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Products.Add(new Product
            {
                Id = 5001,
                ExternalProductId = NayaxExternalId.From(100),
                Name = "Old name",
                QuantityInStock = 7,
            });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var category = await verify.Categories.SingleAsync();
        var product = await verify.Products.SingleAsync();
        Assert.NotEqual(10, category.Id);
        Assert.Equal(category.Id, product.CategoryId);
        // Still the same row: updated in place, with its local stock untouched.
        Assert.Equal((5001L, "Chips", 7), (product.Id, product.Name, product.QuantityInStock));
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
        Assert.Equal(("Chips", "100"), (created.Name, created.ExternalProductId));
        // Its own generated key, which is not the Nayax id and not business B's product's either
        // (issue #735).
        Assert.NotEqual(100L, created.Id);
        Assert.NotEqual(5001L, created.Id);

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
