using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Cross-tenant regression tests for the migrated Nayax product catalogue import (issue #300).
/// The import writes catalogue rows keyed by a remote identifier, so it is exactly the kind of
/// write that must stay inside the caller's business: it reads and writes through the same
/// <c>AppDbContext</c> global query filters and <c>BusinessOwnershipEnforcer</c> every other
/// tenant-owned store uses, never an ad hoc business predicate (AGENTS.md § Tenant ownership).
///
/// Since issue #735 (A4) the catalogue <em>is</em> partitioned per business: a created row's key is
/// the database's own and the provider's id is a column that is unique only within one business, so
/// two operators who sell the same Nayax product each get their own row. That is the limit
/// docs/architecture.md § Tenant ownership recorded for the single-operator Nayax client, and these
/// tests are where it is proven gone - alongside the behaviour that always had to hold: one
/// business's import never reads, rewrites or deletes another business's catalogue row.
/// </summary>
public class EfNayaxProductCatalogImportStoreTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 2, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfNayaxProductCatalogImportStoreTenancyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        // Both sell through Nayax (issue #759): each is a business whose catalogue a Nayax import
        // may write, which is what makes the isolation below the tenant filter's work rather than a
        // missing provider's.
        setup.Businesses.AddRange(NayaxBusiness(BusinessA, "Vending A"), NayaxBusiness(BusinessB, "Vending B"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Imported_rows_are_stamped_with_the_importing_business_and_leave_the_other_untouched()
    {
        using (var seed = TestAppDbContext.For(_options, BusinessB))
        {
            seed.Categories.Add(new Category { Id = 20, Name = "B drinks", Description = "B drinks" });
            seed.Products.Add(new Product { Id = 200, Name = "B cola", UnitPrice = 2m, CategoryId = 20 });
            await seed.SaveChangesAsync();
        }

        await ImportAs(BusinessA, new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "A snacks", "A snacks")],
            [new ImportedProductCatalogEntry(100, "A chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var imported = await verify.Products.SingleAsync(product => product.Name == "A chips");
        var importedCategory = await verify.Categories.SingleAsync(category => category.Name == "A snacks");
        Assert.Equal(BusinessA, imported.BusinessId);
        Assert.Equal(BusinessA, importedCategory.BusinessId);
        Assert.Equal(importedCategory.Id, imported.CategoryId);

        var other = await verify.Products.SingleAsync(product => product.Id == 200);
        Assert.Equal(BusinessB, other.BusinessId);
        Assert.Equal("B cola", other.Name);
        Assert.Equal(2m, other.UnitPrice);

        // The provider ids the import records are on the rows it created, so they are confined by
        // exactly the same stamp (issues #732 and #759): business B's catalogue gains none, so
        // another business's import can neither claim B's rows nor record a Nayax id inside B.
        Assert.Equal("100", imported.ExternalProductId);
        Assert.Equal("10", importedCategory.ExternalCategoryId);
        Assert.Null(other.ExternalProductId);
        Assert.Null((await verify.Categories.SingleAsync(category => category.Id == 20)).ExternalCategoryId);
    }

    /// <summary>
    /// Two businesses importing the <strong>same</strong> Nayax product and group ids - the
    /// acceptance criterion of issue #735 (A4), and the shape that used to be impossible. A key the
    /// database assigns is each row's own, and the external id is unique only per business, so each
    /// operator's catalogue holds its own product 100 and its own group 10 with its own name, price
    /// and category.
    ///
    /// <para>Before this change the second import collided on the shared primary key: A's import
    /// either failed with a <c>DbUpdateException</c> or, had the read not been tenant-scoped, would
    /// have rewritten B's catalogue. The ordering here is deliberate - B imports first, so A is the
    /// business that would have been refused.</para>
    /// </summary>
    [Fact]
    public async Task Two_businesses_import_the_same_nayax_product_and_group_ids_without_conflict()
    {
        await ImportAs(BusinessB, SameIdsNamedFor("B"));
        await ImportAs(BusinessA, SameIdsNamedFor("A"));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var products = await verify.Products.OrderBy(product => product.BusinessId).ToListAsync();
        var categories = await verify.Categories.OrderBy(category => category.BusinessId).ToListAsync();

        // One product and one category per business, each carrying the same Nayax id on its own row
        // under its own generated key.
        Assert.Equal([BusinessA, BusinessB], products.Select(product => product.BusinessId));
        Assert.Equal(["A chips", "B chips"], products.Select(product => product.Name));
        Assert.Equal(["100", "100"], products.Select(product => product.ExternalProductId));
        Assert.Equal(2, products.Select(product => product.Id).Distinct().Count());

        Assert.Equal([BusinessA, BusinessB], categories.Select(category => category.BusinessId));
        Assert.Equal(["10", "10"], categories.Select(category => category.ExternalCategoryId));
        Assert.Equal(2, categories.Select(category => category.Id).Distinct().Count());

        // And each product is in its own business's category, not in the other's.
        Assert.All(
            products,
            product => Assert.Equal(
                categories.Single(category => category.BusinessId == product.BusinessId).Id,
                product.CategoryId));
    }

    /// <summary>
    /// The import's read is tenant-scoped, so another business's product is not a row this import
    /// may update - and since issue #735 nothing about the keys makes that accidental either: A's
    /// entry creates A's own row, and B's product, which carries the same recorded Nayax id, is
    /// exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_recorded_id_another_business_owns_is_never_rewritten_by_this_business()
    {
        using (var seed = TestAppDbContext.For(_options, BusinessB))
        {
            seed.Products.Add(new Product
            {
                Id = 100,
                ExternalProductId = NayaxExternalId.From(100),
                Name = "B cola",
                Description = "B description",
                UnitPrice = 2m,
            });
            await seed.SaveChangesAsync();
        }

        await ImportAs(BusinessA, new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "A chips", "Salted", 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var other = await verify.Products.SingleAsync(product => product.Id == 100);
        Assert.Equal(BusinessB, other.BusinessId);
        Assert.Equal("B cola", other.Name);
        Assert.Equal("B description", other.Description);
        Assert.Equal(2m, other.UnitPrice);

        var imported = await verify.Products.SingleAsync(product => product.BusinessId == BusinessA);
        Assert.Equal(("A chips", "100"), (imported.Name, imported.ExternalProductId));
        Assert.NotEqual(100L, imported.Id);
    }

    /// <summary>
    /// The count issue #736 refuses a manual-catalogue business's import over: the products of
    /// <em>this</em> business that carry no provider id. Another business's manual products are not
    /// this business's problem, which is the tenant filter's work again.
    /// </summary>
    [Fact]
    public async Task Products_with_no_provider_id_are_counted_per_business()
    {
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.AddRange(
                new Product { BusinessId = BusinessA, Name = "A manual" },
                new Product { BusinessId = BusinessA, Name = "A also manual" },
                new Product { BusinessId = BusinessA, Name = "A imported", ExternalProductId = "100" },
                new Product { BusinessId = BusinessB, Name = "B manual" });
            await seed.SaveChangesAsync();
        }

        await using (var a = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(
                2,
                await new EfNayaxProductCatalogImportStore(a)
                    .CountProductsWithNoProviderIdAsync(CancellationToken.None));
        }

        await using var b = TestAppDbContext.For(_options, BusinessB);
        Assert.Equal(
            1,
            await new EfNayaxProductCatalogImportStore(b)
                .CountProductsWithNoProviderIdAsync(CancellationToken.None));
    }

    /// <summary>
    /// One snapshot of the same Nayax product id and group id, named for the business importing it
    /// so each row's own catalogue fields are visible afterwards.
    /// </summary>
    private static NayaxProductCatalogImport SameIdsNamedFor(string prefix) =>
        new(
            [new ImportedProductCategory(10, $"{prefix} snacks", $"{prefix} snacks")],
            [new ImportedProductCatalogEntry(100, $"{prefix} chips", "Salted", 3.50m, 10)],
            ImportedAt);

    private async Task ImportAs(int businessId, NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
