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
/// upsert the Nayax catalogue import performs is a real insert/update against SQLite, so the
/// new/existing split, the repeated-import case and the locally owned fields the import must not
/// touch are proven against the database rather than an in-memory provider.
///
/// Since issue #735 (A4) the database is also what assigns a created row's key, which is itself
/// relational behaviour: the import writes no <c>Id</c>, and what SQLite then puts there - and what
/// the created category's foreign key resolves to - is only observable against a real database.
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
        // The business sells through Nayax (issue #759), which is what storing its Nayax credentials
        // records and what makes the external id columns this import reads and writes Nayax ids.
        setup.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = DateTime.UtcNow,
        });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The created rows carry the catalogue fields, their Nayax ids in the external id columns, and
    /// keys the <strong>database</strong> assigned (issue #735, parent #329 decision A4) - which this
    /// fixture can state exactly, because an empty table makes the generated keys 1 while the Nayax
    /// ids are 100 and 10. The product's category is the created category, named by its generated
    /// key rather than by the group id.
    /// </summary>
    [Fact]
    public async Task A_new_product_and_its_category_are_created_with_database_generated_ids()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        var category = await verify.Categories.SingleAsync();

        Assert.Equal(1, product.Id);
        Assert.Equal("100", product.ExternalProductId);
        Assert.Equal("Chips", product.Name);
        Assert.Equal("Salted", product.Description);
        Assert.Equal(3.50m, product.UnitPrice);
        Assert.Equal(category.Id, product.CategoryId);
        Assert.Equal(0, product.RestockTo);
        Assert.Equal(ImportedAt, product.CreatedAt);
        Assert.Equal(ImportedAt, product.UpdatedAt);

        Assert.Equal(1, category.Id);
        Assert.Equal("10", category.ExternalCategoryId);
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
            // The Nayax id issue #732's backfill recorded for a row that already existed, as issue
            // #759's migration copied it onto the row. Since issue #745 it is what makes this an
            // *existing* product for the snapshot entry below; the local id and the Nayax id are the
            // same number here, as the backfill wrote them.
            seed.Products.Add(new Product
            {
                Id = 100,
                ExternalProductId = NayaxExternalId.From(100),
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
            // The backfilled Nayax id (issue #732, on the row since #759), which is what names this
            // category for the group id in the snapshot below since issue #745.
            seed.Categories.Add(new Category
            {
                Id = 10,
                ExternalCategoryId = NayaxExternalId.From(10),
                Name = "Local name",
                Description = "Local description",
            });
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
    /// One snapshot naming the same product and the same group twice converges inside a single
    /// import: the row created for the first occurrence is what the second one updates.
    ///
    /// <para>Since issue #735 that is also the one thing standing between a repeated id and two
    /// rows carrying it, where the primary key used to refuse the second insert outright - and the
    /// filtered unique index over the external id column, which would then fail the whole import,
    /// is a worse answer than converging.</para>
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_repeats_an_id_creates_one_row_for_it()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks"), new ImportedProductCategory(10, "Snacks again", null)],
            [
                new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10),
                new ImportedProductCatalogEntry(100, "Chips (large)", "Salted, 100g", 4m, 10),
            ],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        var category = await verify.Categories.SingleAsync();
        Assert.Equal(("Chips (large)", 4m, "100"), (product.Name, product.UnitPrice, product.ExternalProductId));
        Assert.Equal(category.Id, product.CategoryId);
        // The first occurrence is the one that created the category; the second named the same group.
        Assert.Equal(("Snacks", "10"), (category.Name, category.ExternalCategoryId));
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
    /// The import is the one writer of a provider id outside the migrations (issues #732 and #759): a
    /// row it creates must arrive already carrying it, or the catalogue would grow rows the later
    /// lookups (#733, #745, #747, #734) cannot resolve even though the id is sitting in the snapshot.
    /// </summary>
    [Fact]
    public async Task A_created_product_and_category_arrive_with_their_nayax_id_recorded()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        var product = await verify.Products.SingleAsync();
        Assert.Equal((1, "100"), (product.BusinessId, product.ExternalProductId));

        var category = await verify.Categories.SingleAsync();
        Assert.Equal((1, "10"), (category.BusinessId, category.ExternalCategoryId));
    }

    /// <summary>
    /// The link tables issue #732 added are no longer written (issue #759): they stay in the schema
    /// only until #760 drops them, and a row this import creates must leave them exactly as it found
    /// them - otherwise they would start to look maintained again.
    /// </summary>
    [Fact]
    public async Task An_import_writes_no_provider_link_row()
    {
        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);
        Assert.Empty(await verify.ProductExternalIds.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.CategoryExternalIds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A local row that carries no external id is not claimed by a snapshot id that merely equals its
    /// key (issue #745): identity comes from the recorded provider id, and since issue #735 (A4) the
    /// new row the entry creates needs no key of the provider's, so the import simply creates it and
    /// leaves the local row completely alone.
    ///
    /// <para>That is the outcome this slice exists for. Merging the remote product into whichever
    /// local row happened to hold its number would be a silent error in financial data - the
    /// operator's own product would start taking a Nayax product's name, price and category - and
    /// refusing the whole import, which is what #745 had to do while the key was the provider's
    /// number, stopped an operator's catalogue over a number collision it did not cause.</para>
    /// </summary>
    [Fact]
    public async Task A_local_row_with_an_equal_key_is_untouched_and_the_entry_creates_its_own_row()
    {
        using (var seed = TestAppDbContext.For(_options, 1))
        {
            seed.Categories.Add(new Category { Id = 10, Name = "Snacks" });
            seed.Products.Add(new Product { Id = 100, Name = "Old name", UnitPrice = 9m, QuantityInStock = 4 });
            await seed.SaveChangesAsync();
        }

        await Apply(new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.For(_options, 1);

        var local = await verify.Products.AsNoTracking().SingleAsync(product => product.Id == 100);
        Assert.Equal(("Old name", null, 9m, 4), (local.Name, local.ExternalProductId, local.UnitPrice, local.QuantityInStock));
        var localCategory = await verify.Categories.AsNoTracking().SingleAsync(category => category.Id == 10);
        Assert.Equal(("Snacks", null), (localCategory.Name, localCategory.ExternalCategoryId));

        // The imported rows: their own generated keys, their Nayax ids recorded, and the product in
        // the category this import created rather than in the local one that shares its number.
        var imported = await verify.Products.AsNoTracking().SingleAsync(product => product.Id != 100);
        var importedCategory = await verify.Categories.AsNoTracking().SingleAsync(category => category.Id != 10);
        Assert.Equal(("Chips", "100"), (imported.Name, imported.ExternalProductId));
        Assert.Equal(importedCategory.Id, imported.CategoryId);
        Assert.Equal(("Snacks", "10"), (importedCategory.Name, importedCategory.ExternalCategoryId));
    }

    /// <summary>
    /// Repeating an import is the normal operator action, and the recorded id is kept rather than
    /// rewritten: it is the same id, and rewriting it would be the one way this import could move an
    /// identifier between rows.
    /// </summary>
    [Fact]
    public async Task A_repeated_import_keeps_the_recorded_nayax_id()
    {
        var import = new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "Snacks", "Snacks")],
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            ImportedAt);

        await Apply(import);
        await Apply(import);

        using var verify = TestAppDbContext.For(_options, 1);
        Assert.Equal("100", (await verify.Products.SingleAsync()).ExternalProductId);
        Assert.Equal("10", (await verify.Categories.SingleAsync()).ExternalCategoryId);
    }

    /// <summary>
    /// The external id columns hold the id issued by the business's own sales provider (issue #759),
    /// so a business that does not sell through Nayax is refused by name rather than having Nayax
    /// ids written into a column another system's ids would be read from. It cannot happen while
    /// storing Nayax credentials is what sets the provider - which is exactly why the refusal is
    /// explicit instead of assumed.
    /// </summary>
    [Fact]
    public async Task An_import_into_a_business_that_does_not_sell_through_Nayax_is_refused()
    {
        using (var withoutProvider = TestAppDbContext.Unrestricted(_options))
        {
            withoutProvider.Businesses.Add(new Business
            {
                Id = 2,
                Name = "Vending B",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await withoutProvider.SaveChangesAsync();
        }

        using var db = TestAppDbContext.For(_options, 2);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EfNayaxProductCatalogImportStore(db).ApplyAsync(
                new NayaxProductCatalogImport(
                    [new ImportedProductCategory(10, "Snacks", "Snacks")],
                    [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
                    ImportedAt),
                CancellationToken.None));

        Assert.Contains("sales provider is not Nayax", exception.Message);

        using var verify = TestAppDbContext.For(_options, 2);
        Assert.Empty(await verify.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Categories.AsNoTracking().ToListAsync());
    }

    private async Task Apply(NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, 1);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
