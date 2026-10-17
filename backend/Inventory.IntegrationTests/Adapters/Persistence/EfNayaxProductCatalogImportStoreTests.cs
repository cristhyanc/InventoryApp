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
/// Relational coverage for <see cref="EfNayaxProductCatalogImportStore"/> (issue #300): the
/// upsert the Nayax catalogue import performs is a real insert/update against SQLite, where the
/// remote identifier is the local primary key, so the new/existing split, the repeated-import case
/// and the locally owned fields the import must not touch are proven against the database rather
/// than an in-memory provider.
/// </summary>
public class EfNayaxProductCatalogImportStoreTests : IDisposable
{
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 2, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfNayaxProductCatalogImportStoreTests()
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

    [Fact]
    public async Task A_new_product_and_its_category_are_created_from_the_snapshot()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal(100, product.Id);
        Assert.Equal("Chips", product.Name);
        Assert.Equal("Salted", product.Description);
        Assert.Equal(3.50m, product.UnitPrice);
        Assert.Equal(10, product.CategoryId);
        Assert.Equal(0, product.RestockTo);
        Assert.Equal(ImportedAt, product.CreatedAt);
        Assert.Equal(ImportedAt, product.UpdatedAt);

        var category = await verify.Categories.SingleAsync();
        Assert.Equal(10, category.Id);
        Assert.Equal("Snacks", category.Name);
        Assert.Equal("Snacks", category.Description);
    }

    /// <summary>
    /// The import owns four catalogue fields. Stock, costing and the operator's own catalogue
    /// edits are local state the Nayax catalogue has no say in, so they must survive an import.
    /// </summary>
    [Fact]
    public async Task An_existing_product_keeps_its_local_stock_and_costing_state()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Products.Add(new Product
            {
                Id = 100,
                Name = "Old name",
                Description = "Old description",
                Sku = "SKU-100",
                UnitPrice = 1m,
                AverageUnitCost = 1.25m,
                CostingQuantity = 7,
                InventoryValue = 8.75m,
                QuantityInStock = 7,
                LowStockThreshold = 3,
                RestockTo = 12,
                SupplierId = null,
                CreatedAt = created,
                UpdatedAt = created,
            });
            await seed.SaveChangesAsync();
            // The link issue #732's backfill recorded for a row that already existed. Since issue
            // #745 it is what makes this an *existing* product for the snapshot entry below; the
            // local id and the Nayax id are the same number here, as the backfill wrote them.
            seed.ProductExternalIds.Add(NayaxProductLink(100));
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal("Chips", product.Name);
        Assert.Equal("Salted", product.Description);
        Assert.Equal(3.50m, product.UnitPrice);
        Assert.Equal(ImportedAt, product.UpdatedAt);
        // Local state the catalogue import does not own.
        Assert.Equal("SKU-100", product.Sku);
        Assert.Equal(1.25m, product.AverageUnitCost);
        Assert.Equal(7, product.CostingQuantity);
        Assert.Equal(8.75m, product.InventoryValue);
        Assert.Equal(7, product.QuantityInStock);
        Assert.Equal(3, product.LowStockThreshold);
        Assert.Equal(12, product.RestockTo);
        Assert.Equal(created, product.CreatedAt);
    }

    /// <summary>
    /// A category that already exists is left alone, exactly as the former implementation did: the
    /// import only ever adds a missing one, so a locally renamed category is not overwritten.
    /// </summary>
    [Fact]
    public async Task An_existing_category_is_not_renamed_and_is_not_duplicated()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Categories.Add(new Category { Id = 10, Name = "Local name", Description = "Local description" });
            await seed.SaveChangesAsync();
            // The backfilled link (issue #732), which is what names this category for the group id
            // in the snapshot below since issue #745.
            seed.CategoryExternalIds.Add(NayaxCategoryLink(10));
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var category = await verify.Categories.SingleAsync();
        Assert.Equal("Local name", category.Name);
        Assert.Equal("Local description", category.Description);
    }

    /// <summary>
    /// Repeating the same import is the normal operator action, so it must converge: the second
    /// run updates the rows the first created instead of inserting a duplicate identity.
    /// </summary>
    [Fact]
    public async Task A_repeated_import_updates_instead_of_duplicating()
    {
        var firstRun = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            firstRun));

        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips (large)", "Salted, 100g", 4m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal("Chips (large)", product.Name);
        Assert.Equal(4m, product.UnitPrice);
        Assert.Equal(firstRun, product.CreatedAt);
        Assert.Equal(ImportedAt, product.UpdatedAt);
        Assert.Equal(1, await verify.Categories.CountAsync());
    }

    /// <summary>
    /// A local product Nayax stopped returning is a data-quality signal for the catalog
    /// reconciliation report, not a deletion: the import never removes local history.
    /// </summary>
    [Fact]
    public async Task A_local_product_missing_from_the_snapshot_is_kept()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Products.Add(new Product { Id = 999, Name = "Retired", QuantityInStock = 4 });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "Chips", null, 3.50m, null)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var retired = await verify.Products.SingleAsync(product => product.Id == 999);
        Assert.Equal("Retired", retired.Name);
        Assert.Equal(4, retired.QuantityInStock);
    }

    [Fact]
    public async Task An_empty_snapshot_changes_nothing()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Chips", UnitPrice = 3.50m });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport([], [], ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal("Chips", product.Name);
        Assert.Equal(3.50m, product.UnitPrice);
    }

    /// <summary>
    /// The import is the one writer of a Nayax link outside the backfill (issue #732): a row it
    /// creates must arrive already linked, or the catalogue would grow rows the later lookups
    /// (#733, #745, #747, #734) cannot resolve even though the id is sitting in the snapshot.
    /// </summary>
    [Fact]
    public async Task A_created_product_and_category_arrive_with_their_nayax_link()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var productLink = await verify.ProductExternalIds.SingleAsync();
        Assert.Equal(
            (1, ExternalProvider.Nayax, "100", 100L),
            (productLink.BusinessId, productLink.Provider, productLink.ExternalId, productLink.ProductId));

        var categoryLink = await verify.CategoryExternalIds.SingleAsync();
        Assert.Equal(
            (1, ExternalProvider.Nayax, "10", 10L),
            (categoryLink.BusinessId, categoryLink.Provider, categoryLink.ExternalId, categoryLink.CategoryId));
    }

    /// <summary>
    /// A local row that carries no Nayax link is not claimed by a snapshot id that merely equals its
    /// key (issue #745): identity comes from the link table, and a row this import creates is still
    /// keyed by the Nayax id until #735 (A4), so the two collide on the primary key.
    ///
    /// <para>That is deliberate, and it is what the issue asks for: the alternative is merging a
    /// remote product into whichever local row happens to hold its number, which is a silent
    /// financial-data error rather than a visible failure. The refusal is named and actionable, and
    /// nothing is written - the import runs in one <c>SaveChanges</c> - so a human can link or
    /// re-key the row and import again. Issue #732's backfill linked every row that existed when the
    /// link tables arrived, so reaching this state needs a row restored from an older backup or
    /// created locally with a colliding key.</para>
    /// </summary>
    [Fact]
    public async Task An_unlinked_local_row_is_not_claimed_by_an_equal_nayax_id()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Categories.Add(new Category { Id = 10, Name = "Snacks" });
            seed.Products.Add(new Product { Id = 100, Name = "Old name" });
            await seed.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt)));

        Assert.Contains("has no provider link in this business", exception.Message);

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.AsNoTracking().SingleAsync();
        Assert.Equal("Old name", product.Name);
        Assert.Equal("Snacks", (await verify.Categories.AsNoTracking().SingleAsync()).Name);
        Assert.Empty(await verify.ProductExternalIds.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.CategoryExternalIds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Repeating an import is the normal operator action, and the link is kept rather than
    /// rewritten: a second row would break the one-link-per-product rule, and rewriting the one
    /// that exists would be a write with nothing to change.
    /// </summary>
    [Fact]
    public async Task A_repeated_import_keeps_the_existing_link()
    {
        var import = new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt);

        await Apply(import);
        await Apply(import);

        using var verify = TestAppDbContext.For(_options, 1);
        Assert.Equal(100L, (await verify.ProductExternalIds.SingleAsync()).ProductId);
        Assert.Equal(10L, (await verify.CategoryExternalIds.SingleAsync()).CategoryId);
    }

    /// <summary>
    /// A Nayax product link in the shape issue #732's backfill wrote for pre-existing data: the
    /// Nayax id and the local id are the same number.
    /// </summary>
    private static ProductExternalId NayaxProductLink(long id) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(id),
            ProductId = id,
        };

    /// <summary>The category half of <see cref="NayaxProductLink"/>.</summary>
    private static CategoryExternalId NayaxCategoryLink(long id) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(id),
            CategoryId = id,
        };

    private async Task Apply(NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, 1);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
