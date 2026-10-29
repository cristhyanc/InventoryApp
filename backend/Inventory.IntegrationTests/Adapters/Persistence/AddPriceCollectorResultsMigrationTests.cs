using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPriceCollectorResults</c> (issue #937, parent #817): two platform tables,
/// the identity columns a listing is checked against, the instant a complete search found none, the
/// per-outcome counts a closed run records, and the two columns a search's answer carries.
///
/// <para>Four claims are worth proving. The first is the negative one: a listing and a run that
/// already existed are migrated and read back column for column, with the new columns empty and
/// the counts zero - this migration invents no identity for a listing recorded before one was read,
/// and no history for a run that never reported. The second is the rebuild: both tables are rebuilt
/// here because SQLite cannot add a check constraint to an existing one, so every constraint, index
/// and foreign key they already had has to survive it. The third is the idempotency key, read from
/// the schema as a unique index rather than trusted to the application. The fourth is the tenancy
/// decision: neither new table carries a business column at all.</para>
/// </summary>
public class AddPriceCollectorResultsMigrationTests
{
    private const string PreviousMigration = "20261028031316_AddPriceCollectorWorkItems";

    private const string TargetMigration = "AddPriceCollectorResults";

    private static readonly DateTime Started = new(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_adds_the_tables_and_leaves_the_rows_that_existed_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "PriceCollectorResultRows",
                await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.DoesNotContain(
                "SupplierListingPendingObservations",
                await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.DoesNotContain(
                "Variant",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierListings"));

            await SeedAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.Contains("PriceCollectorResultRows", tables);
            Assert.Contains("SupplierListingPendingObservations", tables);

            // Both arrive empty: the results endpoint is their writer and this migration submits
            // nothing.
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, "PriceCollectorResultRows"));
            Assert.Equal(
                0,
                await MigrationSchemaProbe.RowCountAsync(connection, "SupplierListingPendingObservations"));

            var listing = await after.SupplierListings.AsNoTracking().SingleAsync();

            Assert.Equal("SKU-EXISTING", listing.SupplierProductId);
            Assert.Equal(30, listing.UnitsPerPack);
            Assert.Equal("0012345678905", listing.Barcode);
            Assert.Equal(BarcodeScope.Unit, listing.BarcodeScope);
            Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
            Assert.Equal(Started, listing.CreatedAtUtc);

            // Nothing was invented for it: no variant, no size and no "we looked and it was gone".
            Assert.Null(listing.Variant);
            Assert.Null(listing.SizeValue);
            Assert.Null(listing.SizeUnit);
            Assert.Null(listing.LastNotFoundAtUtc);

            var run = await after.PriceCollectorRuns.AsNoTracking().SingleAsync();

            Assert.Equal(Started, run.StartedAtUtc);
            Assert.Equal(PriceCollectorRunStatus.PartiallySucceeded, run.Status);
            Assert.Equal(
                (40, 37, 3),
                (run.ListingsCheckedCount, run.ObservationsRecordedCount, run.FailureCount));
            Assert.Equal(Started.AddHours(6), run.ExpiresAtUtc);

            // And no per-outcome history either, which is what a run that closed before these
            // counts existed honestly says.
            Assert.Equal(
                (0, 0, 0, 0, 0),
                (run.CompleteSearchCount,
                    run.TruncatedSearchCount,
                    run.NoMatchesSearchCount,
                    run.FailedSearchCount,
                    run.NotSearchedCount));

            var workItem = await after.PriceCollectorWorkItems.AsNoTracking().SingleAsync();

            Assert.Equal(PriceCollectorWorkItemOutcome.Complete, workItem.Outcome);
            Assert.Null(workItem.FailureReason);
            Assert.Null(workItem.SubmittedRowCount);
        }
    }

    /// <summary>
    /// The two rebuilds keep every constraint, index and reference the rebuilt tables already had,
    /// and add this issue's own.
    /// </summary>
    [Fact]
    public async Task The_rebuilds_keep_the_existing_constraints_and_add_this_issues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        var listings = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierListings");

        foreach (var constraint in new[]
        {
            "CK_SupplierListings_BarcodeAndScopeTogether",
            "CK_SupplierListings_OneOwner",
            "CK_SupplierListings_PriceStateConsistent",
            "CK_SupplierListings_UnitsPerPackPositive",
            "CK_SupplierListings_SizeAndUnitTogether",
            "CK_SupplierListings_SizeValuePositive",
            "FK_SupplierListings_MasterProducts_MasterProductId",
            "FK_SupplierListings_SupplierPresets_SupplierPresetId",
            "FK_SupplierListings_SupplierPriceObservations_LatestObservationId",
        })
        {
            Assert.Contains(constraint, listings, StringComparison.Ordinal);
        }

        var listingIndexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierListings");
        Assert.Contains("IX_SupplierListings_LatestObservationId", listingIndexes);
        Assert.Contains("IX_SupplierListings_MasterProductId", listingIndexes);
        Assert.Contains("IX_SupplierListings_WaitingKey", listingIndexes);
        Assert.Contains("IX_SupplierListings_SupplierPresetId_SupplierProductId", listingIndexes);

        var runs = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "PriceCollectorRuns");
        Assert.Contains("CK_PriceCollectorRuns_CountsNotNegative", runs, StringComparison.Ordinal);
        Assert.Contains("CK_PriceCollectorRuns_SearchCountsNotNegative", runs, StringComparison.Ordinal);

        var runIndexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "PriceCollectorRuns");
        Assert.Contains("IX_PriceCollectorRuns_StartedAtUtc", runIndexes);
        Assert.Contains("IX_PriceCollectorRuns_Status", runIndexes);

        var pending = await MigrationSchemaProbe.CreateTableSqlAsync(
            connection,
            "SupplierListingPendingObservations");

        foreach (var constraint in new[]
        {
            "CK_SupplierListingPendingObservations_PackPricePositive",
            "CK_SupplierListingPendingObservations_CurrencyIsAud",
            "CK_SupplierListingPendingObservations_GstRateIsAProportion",
            "CK_SupplierListingPendingObservations_PromotionEndNeedsPromotion",
            "CK_SupplierListingPendingObservations_SizeAndUnitTogether",
            "CK_SupplierListingPendingObservations_BarcodeAndScopeTogether",
        })
        {
            Assert.Contains(constraint, pending, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The idempotency key is the database's: one row per run, work item and supplier product id,
    /// as a unique index rather than as a <c>WHERE</c> clause two submissions could both pass.
    /// </summary>
    [Fact]
    public async Task The_idempotency_key_is_a_unique_index()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        Assert.Contains(
            "IX_PriceCollectorResultRows_PriceCollectorRunId_PriceCollectorWorkItemId_SupplierProductId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "PriceCollectorResultRows"));

        // And one held reading per listing, which is "replacing the previous pending one" as a
        // rule the database holds.
        Assert.Contains(
            "IX_SupplierListingPendingObservations_SupplierListingId",
            await MigrationSchemaProbe.IndexNamesAsync(
                connection,
                "SupplierListingPendingObservations"));
    }

    /// <summary>
    /// Both new tables are platform tables: no business column at all, which is this issue's
    /// tenancy decision expressed in the schema rather than only in the model.
    /// </summary>
    [Fact]
    public async Task The_new_tables_carry_no_business_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        Assert.DoesNotContain(
            "BusinessId",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "PriceCollectorResultRows"));
        Assert.DoesNotContain(
            "BusinessId",
            await MigrationSchemaProbe.ColumnNamesAsync(
                connection,
                "SupplierListingPendingObservations"));
    }

    /// <summary>
    /// Reversing it removes the two tables, the three constraints and the eleven columns, and
    /// leaves the listing and the run that existed before the upgrade exactly as they were.
    /// </summary>
    [Fact]
    public async Task Reversing_the_upgrade_removes_what_it_added_and_nothing_else()
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
        Assert.DoesNotContain("PriceCollectorResultRows", tables);
        Assert.DoesNotContain("SupplierListingPendingObservations", tables);

        var listingColumns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierListings");
        Assert.DoesNotContain("Variant", listingColumns);
        Assert.DoesNotContain("SizeValue", listingColumns);
        Assert.DoesNotContain("SizeUnit", listingColumns);
        Assert.DoesNotContain("LastNotFoundAtUtc", listingColumns);

        var runColumns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "PriceCollectorRuns");
        Assert.DoesNotContain("CompleteSearchCount", runColumns);
        Assert.DoesNotContain("NotSearchedCount", runColumns);

        var workItemColumns = await MigrationSchemaProbe.ColumnNamesAsync(
            connection,
            "PriceCollectorWorkItems");
        Assert.DoesNotContain("FailureReason", workItemColumns);
        Assert.DoesNotContain("SubmittedRowCount", workItemColumns);

        // Everything the rebuild copied is still there, and so is everything it did not add.
        var listings = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierListings");
        Assert.Contains("CK_SupplierListings_OneOwner", listings, StringComparison.Ordinal);
        Assert.Contains("CK_SupplierListings_PriceStateConsistent", listings, StringComparison.Ordinal);
        Assert.DoesNotContain("CK_SupplierListings_SizeValuePositive", listings, StringComparison.Ordinal);

        Assert.Contains(
            "CK_PriceCollectorRuns_CountsNotNegative",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "PriceCollectorRuns"),
            StringComparison.Ordinal);

        db.ChangeTracker.Clear();

        var listing = await db.SupplierListings
            .AsNoTracking()
            .Select(row => new { row.SupplierProductId, row.UnitsPerPack, row.Barcode, row.MatchStatus })
            .SingleAsync();

        Assert.Equal("SKU-EXISTING", listing.SupplierProductId);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Equal("0012345678905", listing.Barcode);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);

        var run = await db.PriceCollectorRuns
            .AsNoTracking()
            .Select(row => new { row.StartedAtUtc, row.Status, row.ListingsCheckedCount })
            .SingleAsync();

        Assert.Equal(Started, run.StartedAtUtc);
        Assert.Equal(PriceCollectorRunStatus.PartiallySucceeded, run.Status);
        Assert.Equal(40, run.ListingsCheckedCount);
    }

    /// <summary>
    /// Writes a master product, a supplier listing, a run and an answered work item through SQL
    /// rather than through the model, because the model under test is the one <em>after</em> the
    /// upgrade: an <c>INSERT</c> built from it would name the columns this migration adds, which
    /// the previous schema does not have.
    /// </summary>
    private static async Task SeedAsync(SqliteConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO MasterProducts
                (Brand, Name, SizeValue, SizeUnit, NormalisedKey, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('Coca-Cola', 'Classic', 375, 1, 'SEED-KEY', $started, $started);
            """,
            ("$started", Started.ToString("O")));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO SupplierPresets
                (Name, NormalisedName, IsEnabled, ServesNationwide, ServedStates, PricesIncludeGst,
                 PriceCheckFreshnessHours, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('Seeded Grocer', 'SEEDED GROCER', 1, 1, '', 0, 30, $started, $started);
            """,
            ("$started", Started.ToString("O")));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO SupplierListings
                (SupplierPresetId, MasterProductId, SupplierProductId, UnitsPerPack, Barcode,
                 BarcodeScope, MatchStatus, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                (SELECT Id FROM SupplierPresets WHERE NormalisedName = 'SEEDED GROCER'),
                (SELECT Id FROM MasterProducts WHERE NormalisedKey = 'SEED-KEY'),
                'SKU-EXISTING', 30, '0012345678905', 1, 2, $started, $started);
            """,
            ("$started", Started.ToString("O")));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO PriceCollectorRuns
                (StartedAtUtc, CompletedAtUtc, ExpiresAtUtc, Status, ListingsCheckedCount,
                 ObservationsRecordedCount, FailureCount)
            VALUES ($started, $completed, $expires, 3, 40, 37, 3);
            """,
            ("$started", Started.ToString("O")),
            ("$completed", Started.AddMinutes(20).ToString("O")),
            ("$expires", Started.AddHours(6).ToString("O")));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO PriceCollectorWorkItems
                (PriceCollectorRunId, SupplierPresetId, MasterProductId, IssuedAtUtc, Outcome,
                 OutcomeAtUtc)
            VALUES (
                (SELECT Id FROM PriceCollectorRuns LIMIT 1),
                (SELECT Id FROM SupplierPresets WHERE NormalisedName = 'SEEDED GROCER'),
                (SELECT Id FROM MasterProducts WHERE NormalisedKey = 'SEED-KEY'),
                $started, 1, $started);
            """,
            ("$started", Started.ToString("O")));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
