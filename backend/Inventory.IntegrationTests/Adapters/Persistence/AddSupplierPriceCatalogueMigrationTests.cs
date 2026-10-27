using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPriceCatalogue</c> (issue #923, parent #817). The migration creates
/// four tables and does nothing else at all.
///
/// <para>The claim worth proving is the negative one, and it is issue #923's last criterion: no
/// existing product, purchase, cost, inventory or report behaviour changes. A database that already
/// holds a business, its products, a purchase of them and a sale is therefore migrated and read back
/// afterwards, column for column - because "additive" is easy to say and a rebuilt table, a new
/// default or a backfilled column would each be a silent way to break it.</para>
/// </summary>
public class AddSupplierPriceCatalogueMigrationTests
{
    private const string PreviousMigration = "20261027110241_AddSupplierPresetLink";
    private const string TargetMigration = "AddSupplierPriceCatalogue";

    private static readonly string[] CreatedTables =
        ["MasterProducts", "ProductMatchSuggestions", "ProductPriceTrackings", "SupplierListings"];

    [Fact]
    public async Task Migration_creates_the_four_catalogue_tables_and_changes_no_existing_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.All(CreatedTables, table => Assert.DoesNotContain(table, tables));

            // A business with a product, a purchase of it and a completed sale: the rows whose
            // behaviour this migration must not touch. Written as SQL rather than through the EF
            // model, because the model describes the schema *after* every migration and this database
            // deliberately stops one short.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Id, Name, TimeZoneId, Status, GstRegistered, CreatedAtUtc)
                    VALUES (1, 'Coastal Vending', 'Australia/Sydney', 20, 1, '2026-02-10 04:00:00');
                INSERT INTO Products (Id, BusinessId, Name, Unit, AverageUnitCost, CostingQuantity,
                                      InventoryValue, QuantityInStock, LowStockThreshold, RestockTo,
                                      IsActive, GstRule, CreatedAt, UpdatedAt)
                    VALUES (7, 1, 'Coca-Cola Classic', '375mL', 0.85, 24, 20.4, 24, 6, 48, 1, 0,
                            '2026-02-10 04:00:00', '2026-02-10 04:00:00');
                INSERT INTO Suppliers (Id, BusinessId, Name, ProductLineGstDefault, DeliveryGstDefault,
                                       PackageGstDefault, IsHidden)
                    VALUES (3, 1, 'Alpha Grocer', 0, 0, 0, 0);
                INSERT INTO Receipts (Id, BusinessId, SupplierId, Title, PurchaseDate, TotalAmount,
                                      DeliveryGstClassification, DeliveryGstClassificationSource,
                                      PackageGstClassification, PackageGstClassificationSource,
                                      FileName, StoredFileName, ContentType, FileSizeBytes,
                                      CreatedAt)
                    VALUES (11, 1, 3, 'February stock', '2026-02-11 00:00:00', 20.40, 0, 0, 0, 0,
                            'invoice.pdf', 'stored-invoice.pdf', 'application/pdf', 1024,
                            '2026-02-11 04:00:00');
                INSERT INTO ReceiptItems (Id, BusinessId, ReceiptId, ProductId, Quantity, UnitCost,
                                          GstClassification, GstClassificationSource)
                    VALUES (13, 1, 11, 7, 24, 0.85, 0, 0);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.All(CreatedTables, table => Assert.Contains(table, tables));

            // The four tables arrive empty. Nothing is seeded here, unlike AddSupplierPresets: a
            // master product is a description somebody has to decide, not a list this application can
            // ship on anybody's behalf.
            Assert.Empty(await after.MasterProducts.AsNoTracking().ToListAsync());
            Assert.Empty(await after.SupplierListings.AsNoTracking().ToListAsync());
            Assert.Empty(await after.ProductMatchSuggestions.AsNoTracking().ToListAsync());
            Assert.Empty(await after.ProductPriceTrackings.AsNoTracking().ToListAsync());

            // And the rows that were already there are exactly as they were - the product's costing
            // values above all, which are the ones AGENTS.md treats as financial data.
            var product = await after.Products
                .AsNoTracking()
                .Select(row => new
                {
                    row.Id,
                    row.BusinessId,
                    row.Name,
                    row.Unit,
                    row.AverageUnitCost,
                    row.CostingQuantity,
                    row.InventoryValue,
                    row.QuantityInStock,
                    row.LowStockThreshold,
                    row.RestockTo,
                    row.IsActive,
                })
                .SingleAsync();

            Assert.Equal(7L, product.Id);
            Assert.Equal(1, product.BusinessId);
            Assert.Equal("Coca-Cola Classic", product.Name);
            Assert.Equal("375mL", product.Unit);
            Assert.Equal(0.85m, product.AverageUnitCost);
            Assert.Equal(24, product.CostingQuantity);
            Assert.Equal(20.4m, product.InventoryValue);
            Assert.Equal(24, product.QuantityInStock);
            Assert.Equal(6, product.LowStockThreshold);
            Assert.Equal(48, product.RestockTo);
            Assert.True(product.IsActive);

            var purchaseItem = await after.ReceiptItems
                .AsNoTracking()
                .Select(row => new { row.Id, row.ReceiptId, row.ProductId, row.Quantity, row.UnitCost })
                .SingleAsync();

            Assert.Equal(13, purchaseItem.Id);
            Assert.Equal(11, purchaseItem.ReceiptId);
            Assert.Equal(7L, purchaseItem.ProductId);
            Assert.Equal(24, purchaseItem.Quantity);
            Assert.Equal(0.85m, purchaseItem.UnitCost);

            // The products table itself is untouched: no column added, and in particular none of the
            // catalogue's own columns leaked onto it.
            var productColumns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products");
            Assert.DoesNotContain("MasterProductId", productColumns);
            Assert.DoesNotContain("WaitingKey", productColumns);
            Assert.Contains("AverageUnitCost", productColumns);
        }
    }

    /// <summary>
    /// The mapping table is tenant-owned, so it arrives with the business key, the index the central
    /// mechanism adds, and the per-business unique index - and the catalogue's three platform tables
    /// arrive with <strong>no</strong> business column at all, which is the tenancy decision of this
    /// issue expressed in the schema rather than only in the model.
    /// </summary>
    [Fact]
    public async Task The_schema_states_which_of_the_four_tables_belongs_to_a_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        Assert.Contains(
            "BusinessId",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "ProductPriceTrackings"));
        Assert.Contains(
            "IX_ProductPriceTrackings_BusinessId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "ProductPriceTrackings"));
        Assert.Contains(
            "IX_ProductPriceTrackings_BusinessId_ProductId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "ProductPriceTrackings"));

        foreach (var platformTable in new[] { "MasterProducts", "SupplierListings", "ProductMatchSuggestions" })
        {
            Assert.DoesNotContain(
                "BusinessId",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, platformTable));
        }
    }

    /// <summary>
    /// The constraints the issue asks the database itself to hold, read back from the schema rather
    /// than only exercised through it: the exclusive ownership of a listing, the pack, size and
    /// barcode rules, and one proposal per suggestion. <c>SupplierPriceCatalogueConstraintTests</c>
    /// proves what each of them refuses; this proves the upgrade is what put them there.
    /// </summary>
    [Fact]
    public async Task The_upgrade_declares_the_catalogue_s_check_constraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        var listings = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierListings");
        Assert.Contains("CK_SupplierListings_OneOwner", listings, StringComparison.Ordinal);
        Assert.Contains("CK_SupplierListings_UnitsPerPackPositive", listings, StringComparison.Ordinal);
        Assert.Contains("CK_SupplierListings_BarcodeAndScopeTogether", listings, StringComparison.Ordinal);

        // The origin waiting key is a column of its own, and deliberately not part of the ownership
        // check: that is what lets a matched listing still record which search found it.
        Assert.Contains(
            "OriginWaitingKey",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierListings"));

        Assert.Contains(
            "CK_MasterProducts_SizeValuePositive",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "MasterProducts"),
            StringComparison.Ordinal);

        var suggestions =
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "ProductMatchSuggestions");
        Assert.Contains("CK_ProductMatchSuggestions_OneProposal", suggestions, StringComparison.Ordinal);
        Assert.Contains(
            "CK_ProductMatchSuggestions_SuggestedSizePositive",
            suggestions,
            StringComparison.Ordinal);
        Assert.Contains(
            "CK_ProductMatchSuggestions_ConfidenceIsAProportion",
            suggestions,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Reversing it removes the four tables and nothing else, which is what reversing a purely
    /// additive migration means: there is no data to restore, because none was written.
    /// </summary>
    [Fact]
    public async Task Reversing_the_upgrade_removes_the_four_tables_and_leaves_the_rest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(TargetMigration);
        await migrator.MigrateAsync(PreviousMigration);

        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.All(CreatedTables, table => Assert.DoesNotContain(table, tables));

        // The tables the catalogue referenced are still there, with their own rows' schema intact.
        Assert.Contains("Products", tables);
        Assert.Contains("SupplierPresets", tables);
        Assert.Contains(
            "NormalisedName",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets"));
    }
}
