using Inventory.Application.Imports;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
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
        setup.Businesses.Add(new Business { Id = 1, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow });
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

    private async Task Apply(NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, 1);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
