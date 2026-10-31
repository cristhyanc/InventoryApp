using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPriceCollectorWorkItems</c> (issue #924, parent #817): one platform table,
/// the expiry instant a run carries, and the index that finds the run still open.
///
/// <para>Three claims are worth proving. The first is the negative one: a database that already holds
/// a collector run with its counts and status is migrated and read back column for column, and the
/// run it already had gets <strong>no</strong> expiry - a lifetime is not something a migration may
/// invent for work that was issued before lifetimes existed. The second is the rebuild: adding a
/// column and an index to <c>PriceCollectorRuns</c> must leave #925's own check constraint in place.
/// The third is the tenancy decision, read from the schema rather than only from the model: the new
/// table carries no business column at all.</para>
/// </summary>
public class AddPriceCollectorWorkItemsMigrationTests
{
    private const string PreviousMigration = "20261028011557_AddSupplierPriceObservations";

    private const string TargetMigration = "AddPriceCollectorWorkItems";

    private static readonly DateTime Started = new(2026, 10, 27, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_creates_the_work_item_table_and_changes_no_existing_run()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "PriceCollectorWorkItems",
                await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.DoesNotContain(
                "ExpiresAtUtc",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "PriceCollectorRuns"));

            await SeedRunAsync(connection, PriceCollectorRunStatus.PartiallySucceeded, 40, 37, 3);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "PriceCollectorWorkItems",
                await MigrationSchemaProbe.TableNamesAsync(connection));

            // The table arrives empty: issuing a work list is its writer, and this migration issues
            // nothing. Counted through the schema rather than through the model, because the model
            // is today's and this database is at this migration: a later release's columns are not
            // here yet, and asking for them would fail for the wrong reason (issue #937 added two).
            Assert.Equal(
                0,
                await MigrationSchemaProbe.RowCountAsync(connection, "PriceCollectorWorkItems"));

            // Projected for the same reason, to exactly the columns this migration leaves behind.
            var run = await after.PriceCollectorRuns
                .AsNoTracking()
                .Select(row => new
                {
                    row.StartedAtUtc,
                    row.CompletedAtUtc,
                    row.Status,
                    row.ListingsCheckedCount,
                    row.ObservationsRecordedCount,
                    row.FailureCount,
                    row.ExpiresAtUtc,
                })
                .SingleAsync();

            Assert.Equal(Started, run.StartedAtUtc);
            Assert.Equal(Started.AddMinutes(20), run.CompletedAtUtc);
            Assert.Equal(PriceCollectorRunStatus.PartiallySucceeded, run.Status);
            Assert.Equal((40, 37, 3), (run.ListingsCheckedCount, run.ObservationsRecordedCount, run.FailureCount));

            // And no lifetime was invented for it. A run with no expiry is one nothing expires
            // automatically, which is the only honest answer about a run issued before this release.
            Assert.Null(run.ExpiresAtUtc);
        }
    }

    /// <summary>
    /// The rebuild that adding the column and the index causes leaves #925's own check constraint on
    /// the runs table exactly where it was, and declares the new table's two.
    /// </summary>
    [Fact]
    public async Task The_upgrade_declares_the_work_item_constraints_and_keeps_the_runs_own()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        var workItems =
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "PriceCollectorWorkItems");

        Assert.Contains("CK_PriceCollectorWorkItems_OneProduct", workItems, StringComparison.Ordinal);
        Assert.Contains(
            "CK_PriceCollectorWorkItems_OutcomeAndInstantTogether",
            workItems,
            StringComparison.Ordinal);

        // Every reference restricts: work issued against a run, a supplier or a master product cannot
        // be orphaned by deleting what it names.
        Assert.Contains("PriceCollectorRuns", workItems, StringComparison.Ordinal);
        Assert.Contains("SupplierPresets", workItems, StringComparison.Ordinal);
        Assert.Contains("MasterProducts", workItems, StringComparison.Ordinal);

        Assert.Contains(
            "CK_PriceCollectorRuns_CountsNotNegative",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "PriceCollectorRuns"),
            StringComparison.Ordinal);

        // One work item per product and supplier in a run is the database's rule, not a WHERE clause
        // two concurrent issues could both pass - and it is two filtered indexes, because SQLite
        // treats NULLs as distinct in a unique index and one of the two key columns is always NULL.
        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "PriceCollectorWorkItems");
        Assert.Contains(
            "IX_PriceCollectorWorkItems_PriceCollectorRunId_SupplierPresetId_MasterProductId",
            indexes);
        Assert.Contains(
            "IX_PriceCollectorWorkItems_PriceCollectorRunId_SupplierPresetId_WaitingKey",
            indexes);
        Assert.Contains(
            "IX_PriceCollectorRuns_Status",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "PriceCollectorRuns"));
    }

    /// <summary>
    /// The new table is a platform table: no business column at all, which is this issue's tenancy
    /// decision expressed in the schema rather than only in the model.
    /// </summary>
    [Fact]
    public async Task The_new_table_carries_no_business_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        Assert.DoesNotContain(
            "BusinessId",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "PriceCollectorWorkItems"));
    }

    /// <summary>
    /// Reversing it removes the table, the index and the column and leaves the run that existed before
    /// the upgrade exactly as it was - which is what reversing an additive migration means.
    /// </summary>
    [Fact]
    public async Task Reversing_the_upgrade_removes_the_table_and_the_added_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);

        await SeedRunAsync(connection, PriceCollectorRunStatus.Succeeded, 12, 12, 0);

        await migrator.MigrateAsync(TargetMigration);
        await migrator.MigrateAsync(PreviousMigration);

        Assert.DoesNotContain(
            "PriceCollectorWorkItems",
            await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.DoesNotContain(
            "ExpiresAtUtc",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "PriceCollectorRuns"));
        Assert.DoesNotContain(
            "IX_PriceCollectorRuns_Status",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "PriceCollectorRuns"));

        db.ChangeTracker.Clear();

        var run = await db.PriceCollectorRuns
            .AsNoTracking()
            .Select(row => new
            {
                row.StartedAtUtc,
                row.CompletedAtUtc,
                row.Status,
                row.ListingsCheckedCount,
                row.ObservationsRecordedCount,
                row.FailureCount,
            })
            .SingleAsync();

        Assert.Equal(Started, run.StartedAtUtc);
        Assert.Equal(PriceCollectorRunStatus.Succeeded, run.Status);
        Assert.Equal(12, run.ListingsCheckedCount);
    }

    /// <summary>
    /// Writes one collector run through SQL rather than through the model, because the model under
    /// test is the one <em>after</em> the upgrade: an <c>INSERT</c> built from it would name the
    /// column this migration adds, which the previous schema does not have. Naming the columns
    /// explicitly is also what makes the test's own statement survive the migration it is about.
    /// </summary>
    private static async Task SeedRunAsync(
        SqliteConnection connection,
        PriceCollectorRunStatus status,
        int listingsChecked,
        int observationsRecorded,
        int failures)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "PriceCollectorRuns"
                ("StartedAtUtc", "CompletedAtUtc", "Status", "ListingsCheckedCount",
                 "ObservationsRecordedCount", "FailureCount")
            VALUES ($started, $completed, $status, $checked, $recorded, $failures);
            """;
        command.Parameters.AddWithValue("$started", Started.ToString("O"));
        command.Parameters.AddWithValue("$completed", Started.AddMinutes(20).ToString("O"));
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$checked", listingsChecked);
        command.Parameters.AddWithValue("$recorded", observationsRecorded);
        command.Parameters.AddWithValue("$failures", failures);

        await command.ExecuteNonQueryAsync();
    }
}
