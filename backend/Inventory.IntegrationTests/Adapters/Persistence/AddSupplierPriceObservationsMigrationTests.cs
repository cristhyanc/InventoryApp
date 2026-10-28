using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPriceObservations</c> (issue #925, parent #817): two platform
/// tables, the three columns a listing keeps about its own price checks, the per-supplier freshness
/// limit, and one decided value.
///
/// <para>Two claims are worth proving. The first is the negative one: no existing product, purchase,
/// cost, inventory or report behaviour changes, so a database that already holds a business, its
/// product, a purchase of it and a supplier listing is migrated and read back column for column. The
/// second is about the <strong>rebuild</strong>: adding a check constraint or a foreign key to an
/// existing table is a table rebuild in SQLite, so <c>SupplierListings</c> and <c>SupplierPresets</c>
/// are both rebuilt here, and "additive" has to survive that - a shipped supplier record whose name,
/// market, price basis or connector came out of the rebuild changed would be a silent way to break
/// it.</para>
/// </summary>
public class AddSupplierPriceObservationsMigrationTests
{
    private const string PreviousMigration = "20261027111451_AddSupplierPriceCatalogue";
    private const string TargetMigration = "AddSupplierPriceObservations";

    private static readonly string[] CreatedTables = ["PriceCollectorRuns", "SupplierPriceObservations"];

    private static readonly string[] AddedListingColumns =
        ["LatestObservationId", "LastCheckedAtUtc", "LastPriceChangeAtUtc"];

    [Fact]
    public async Task Migration_creates_the_two_tables_and_changes_no_existing_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.All(CreatedTables, table => Assert.DoesNotContain(table, tables));
            Assert.DoesNotContain(
                "PriceCheckFreshnessHours",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets"));

            await SeedAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.All(CreatedTables, table => Assert.Contains(table, tables));

            // Both tables arrive empty: the collector import of #937 is their writer, and this
            // migration seeds no price and no run.
            Assert.Empty(await after.PriceCollectorRuns.AsNoTracking().ToListAsync());
            Assert.Empty(await after.SupplierPriceObservations.AsNoTracking().ToListAsync());

            // The product and purchase rows that were already there, column for column - the costing
            // values above all, which are the ones AGENTS.md treats as financial data.
            var product = await after.Products
                .AsNoTracking()
                .Select(row => new
                {
                    row.Id,
                    row.Name,
                    row.AverageUnitCost,
                    row.CostingQuantity,
                    row.InventoryValue,
                    row.QuantityInStock,
                })
                .SingleAsync();

            Assert.Equal(7L, product.Id);
            Assert.Equal("Coca-Cola Classic", product.Name);
            Assert.Equal(0.85m, product.AverageUnitCost);
            Assert.Equal(24, product.CostingQuantity);
            Assert.Equal(20.4m, product.InventoryValue);
            Assert.Equal(24, product.QuantityInStock);

            var purchaseItem = await after.ReceiptItems
                .AsNoTracking()
                .Select(row => new { row.Id, row.ProductId, row.Quantity, row.UnitCost })
                .SingleAsync();

            Assert.Equal(13, purchaseItem.Id);
            Assert.Equal(7L, purchaseItem.ProductId);
            Assert.Equal(24, purchaseItem.Quantity);
            Assert.Equal(0.85m, purchaseItem.UnitCost);

            // And the listing that existed before the rebuild, with its three new columns empty:
            // never checked is the state every listing is in until #937 writes one.
            var listing = await after.SupplierListings.AsNoTracking().SingleAsync();

            Assert.Equal("SKU-1", listing.SupplierProductId);
            Assert.Equal(24, listing.UnitsPerPack);
            Assert.Equal("waiting-key", listing.WaitingKey);
            Assert.Equal("waiting-key", listing.OriginWaitingKey);
            Assert.Equal(SupplierListingMatchStatus.Proposed, listing.MatchStatus);
            Assert.Null(listing.LatestObservationId);
            Assert.Null(listing.LastCheckedAtUtc);
            Assert.Null(listing.LastPriceChangeAtUtc);

            var listingColumns =
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierListings");
            Assert.All(AddedListingColumns, column => Assert.Contains(column, listingColumns));
        }
    }

    /// <summary>
    /// The one decided value this migration writes: BIG W is checked on Thursday and Sunday, so its
    /// record carries 102 hours, and every other record carries the daily 30. The shipped records are
    /// otherwise untouched by the rebuild - which is the half of "additive" a rebuilt table could
    /// quietly break.
    /// </summary>
    [Fact]
    public async Task The_upgrade_records_the_twice_weekly_limit_and_leaves_every_other_record_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);

        var before = await db.SupplierPresets
            .AsNoTracking()
            .OrderBy(preset => preset.NormalisedName)
            .Select(preset => new
            {
                preset.NormalisedName,
                preset.Name,
                preset.Url,
                preset.IsEnabled,
                preset.ServesNationwide,
                preset.ServedStates,
                preset.PricesIncludeGst,
                preset.PriceConnector,
                preset.PriceConnectorKey,
                preset.CreatedAtUtc,
                preset.UpdatedAtUtc,
            })
            .ToListAsync();

        Assert.Contains(before, preset => preset.NormalisedName == "BIG W");

        await migrator.MigrateAsync(TargetMigration);

        var after = await db.SupplierPresets
            .AsNoTracking()
            .OrderBy(preset => preset.NormalisedName)
            .ToListAsync();

        Assert.Equal(
            before.Select(preset => preset.NormalisedName),
            after.Select(preset => preset.NormalisedName));

        foreach (var expected in before)
        {
            var stored = after.Single(preset => preset.NormalisedName == expected.NormalisedName);

            // Everything the record already claimed is exactly as it was, rebuild or no rebuild.
            Assert.Equal(expected.Name, stored.Name);
            Assert.Equal(expected.Url, stored.Url);
            Assert.Equal(expected.IsEnabled, stored.IsEnabled);
            Assert.Equal(expected.ServesNationwide, stored.ServesNationwide);
            Assert.Equal(expected.ServedStates, stored.ServedStates);
            Assert.Equal(expected.PricesIncludeGst, stored.PricesIncludeGst);
            Assert.Equal(expected.PriceConnector, stored.PriceConnector);
            Assert.Equal(expected.PriceConnectorKey, stored.PriceConnectorKey);
            Assert.Equal(expected.CreatedAtUtc, stored.CreatedAtUtc);
            Assert.Equal(expected.UpdatedAtUtc, stored.UpdatedAtUtc);

            // And the one new column: the twice-weekly limit on the record whose cadence the owner
            // stated, and the daily default everywhere else.
            Assert.Equal(
                expected.NormalisedName == "BIG W"
                    ? SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours
                    : SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
                stored.PriceCheckFreshnessHours);
        }
    }

    /// <summary>
    /// The new tables are platform tables: no business column at all, which is this issue's tenancy
    /// decision expressed in the schema rather than only in the model.
    /// </summary>
    [Fact]
    public async Task The_new_tables_carry_no_business_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        foreach (var table in CreatedTables)
        {
            Assert.DoesNotContain(
                "BusinessId",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, table));
        }
    }

    /// <summary>
    /// The constraints the issue asks the database itself to hold, read back from the schema rather
    /// than only exercised through it. <c>SupplierPriceObservationStorageTests</c> proves what each
    /// of them refuses; this proves the upgrade is what put them there.
    /// </summary>
    [Fact]
    public async Task The_upgrade_declares_the_price_observation_check_constraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        var observations =
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierPriceObservations");
        Assert.Contains(
            "CK_SupplierPriceObservations_PackPricePositive",
            observations,
            StringComparison.Ordinal);
        Assert.Contains(
            "CK_SupplierPriceObservations_RegularPackPricePositive",
            observations,
            StringComparison.Ordinal);
        Assert.Contains(
            "CK_SupplierPriceObservations_GstRateIsAProportion",
            observations,
            StringComparison.Ordinal);
        Assert.Contains(
            "CK_SupplierPriceObservations_PromotionEndNeedsPromotion",
            observations,
            StringComparison.Ordinal);
        Assert.Contains(
            "CK_SupplierPriceObservations_CurrencyIsAud",
            observations,
            StringComparison.Ordinal);

        Assert.Contains(
            "CK_PriceCollectorRuns_CountsNotNegative",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "PriceCollectorRuns"),
            StringComparison.Ordinal);

        Assert.Contains(
            "CK_SupplierListings_PriceStateConsistent",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierListings"),
            StringComparison.Ordinal);
        Assert.Contains(
            "CK_SupplierPresets_PriceCheckFreshnessHoursInRange",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierPresets"),
            StringComparison.Ordinal);

        // The rebuild did not take #923's own constraints with it.
        var listings = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierListings");
        Assert.Contains("CK_SupplierListings_OneOwner", listings, StringComparison.Ordinal);
        Assert.Contains("CK_SupplierListings_UnitsPerPackPositive", listings, StringComparison.Ordinal);
        Assert.Contains(
            "CK_SupplierListings_BarcodeAndScopeTogether",
            listings,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Reversing it removes the two tables and the four columns and leaves everything else, including
    /// the listing that existed before the upgrade. The BIG W freshness limit goes with the column
    /// that held it, which is what reversing an additive migration means.
    /// </summary>
    [Fact]
    public async Task Reversing_the_upgrade_removes_the_two_tables_and_the_added_columns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);
        await SeedAsync(connection);

        await migrator.MigrateAsync(TargetMigration);
        await migrator.MigrateAsync(PreviousMigration);

        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.All(CreatedTables, table => Assert.DoesNotContain(table, tables));

        var listingColumns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierListings");
        Assert.All(AddedListingColumns, column => Assert.DoesNotContain(column, listingColumns));
        Assert.DoesNotContain(
            "PriceCheckFreshnessHours",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets"));

        // The rows and the tables around them are still there.
        Assert.Contains("Products", tables);
        Assert.Contains("SupplierListings", tables);
        Assert.Contains("SupplierPresets", tables);

        await using var listingCount = connection.CreateCommand();
        listingCount.CommandText = "SELECT COUNT(*) FROM SupplierListings;";
        Assert.Equal(1L, await listingCount.ExecuteScalarAsync());

        await using var presetCount = connection.CreateCommand();
        presetCount.CommandText =
            "SELECT COUNT(*) FROM SupplierPresets WHERE NormalisedName = 'BIG W';";
        Assert.Equal(1L, await presetCount.ExecuteScalarAsync());
    }

    /// <summary>
    /// A business with a product, a purchase of it, and a supplier listing of a master product: the
    /// rows whose behaviour this migration must not touch, and the listing the rebuild has to carry
    /// across unchanged.
    ///
    /// <para>Written as SQL rather than through the EF model, because the model describes the schema
    /// <em>after</em> every migration and this database deliberately stops one short.</para>
    /// </summary>
    private static async Task SeedAsync(SqliteConnection connection)
    {
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
                                  FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt)
                VALUES (11, 1, 3, 'February stock', '2026-02-11 00:00:00', 20.40, 0, 0, 0, 0,
                        'invoice.pdf', 'stored-invoice.pdf', 'application/pdf', 1024,
                        '2026-02-11 04:00:00');
            INSERT INTO ReceiptItems (Id, BusinessId, ReceiptId, ProductId, Quantity, UnitCost,
                                      GstClassification, GstClassificationSource)
                VALUES (13, 1, 11, 7, 24, 0.85, 0, 0);
            INSERT INTO SupplierListings (SupplierPresetId, MasterProductId, WaitingKey,
                                          OriginWaitingKey, SupplierProductId, UnitsPerPack,
                                          MatchStatus, CreatedAtUtc, UpdatedAtUtc)
                SELECT Id, NULL, 'waiting-key', 'waiting-key', 'SKU-1', 24, 1,
                       '2026-10-27 05:00:00', '2026-10-27 05:00:00'
                FROM SupplierPresets WHERE NormalisedName = 'BIG W';
            """;

        await seed.ExecuteNonQueryAsync();
    }
}
