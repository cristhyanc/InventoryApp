using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddReceiptScanUsage</c> (issue #1164, sub-issue C of #975): the counter table
/// and its two indexes arrive, nothing recorded about a purchase is touched, and rolling back takes
/// exactly that table away again.
///
/// <para>The migration is purely additive and backfills <strong>nothing</strong>. There is nothing to
/// backfill: a scan leaves no record of itself anywhere by design, so no business's past scanning can
/// be reconstructed and none is guessed at - every business starts the month the cap is deployed in
/// with a count of zero. That is the part a reviewer cannot take on trust, because a migration that
/// invented a starting count would refuse scans nobody had made.</para>
///
/// <para>The column list is asserted in full, because what is <em>absent</em> from it is the privacy
/// rule: there is no column that could carry a file name, a supplier, a product, an amount, a draft
/// or an outcome. A row says that <em>n</em> scans happened in a month and nothing about what was on
/// them.</para>
///
/// <para>Relational SQLite rather than InMemory: a created table, a unique index and preserved rows
/// across an upgrade are relational facts.</para>
/// </summary>
public class AddReceiptScanUsageMigrationTests
{
    private const string PreviousMigration = "20261110000000_AddPurchaseInvoiceNumber";
    private const string TargetMigration = "AddReceiptScanUsage";
    private const string UsageTable = "ReceiptScanUsages";

    [Fact]
    public async Task The_upgrade_adds_the_counter_table_with_its_business_and_month_indexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(UsageTable, await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, TargetMigration);

        Assert.Contains(UsageTable, await MigrationSchemaProbe.TableNamesAsync(connection));

        // A business, a month, a count and two instants. Nothing about any receipt.
        Assert.Equal(
            [
                "BusinessId",
                "CreatedAtUtc",
                "Id",
                "MonthKey",
                "ScanCount",
                "UpdatedAtUtc",
            ],
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, UsageTable))
                .Order(StringComparer.Ordinal));

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, UsageTable);
        // The central tenant index every tenant-owned table gets, and the unique one that is the
        // cap's backstop: one business cannot come to hold two counters for one month.
        Assert.Contains($"IX_{UsageTable}_BusinessId", indexes);
        Assert.Contains($"IX_{UsageTable}_BusinessId_MonthKey", indexes);
    }

    /// <summary>
    /// The data half: a purchase and its document recorded under the previous schema come through
    /// with every value they had, and the new table arrives <strong>empty</strong> - no business is
    /// credited or charged with scans it never made.
    /// </summary>
    [Fact]
    public async Task Recorded_purchases_survive_the_upgrade_and_no_business_starts_with_a_count()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Businesses" ("Id", "Name", "Status", "CreatedAtUtc", "TimeZoneId")
            VALUES (1, 'Vending Co', 10, '2026-11-01T00:00:00.0000000Z', 'Australia/Sydney');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Receipts" (
                "Id", "BusinessId", "Title", "PurchaseDate", "CreatedAt",
                "FileName", "StoredFileName", "ContentType", "FileSizeBytes")
            VALUES (
                1, 1, 'Restock', '2026-11-02T00:00:00.0000000Z', '2026-11-02T00:00:00.0000000Z',
                'receipt.jpg', 'stored-receipt.jpg', 'image/jpeg', 3456);
            """);

        await MigrateToAsync(db, TargetMigration);

        await using var verify = TestAppDbContext.Unrestricted(OptionsFor(connection));
        var purchase = await verify.Receipts.AsNoTracking()
            .Select(receipt => new
            {
                receipt.FileName,
                receipt.StoredFileName,
                receipt.ContentType,
                receipt.FileSizeBytes,
            })
            .SingleAsync();
        Assert.Equal(
            ("receipt.jpg", "stored-receipt.jpg", "image/jpeg", 3456L),
            (purchase.FileName, purchase.StoredFileName, purchase.ContentType, purchase.FileSizeBytes));

        Assert.Empty(await verify.ReceiptScanUsages.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Rolling back removes exactly the counter table and leaves the recorded purchase in place.
    /// Counts recorded under the new schema are lost by that rollback, which is the honest
    /// consequence: a release that does not enforce the cap has no counter to keep.
    /// </summary>
    [Fact]
    public async Task Rolling_back_removes_the_counter_table_and_keeps_the_records()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Businesses" ("Id", "Name", "Status", "CreatedAtUtc", "TimeZoneId")
            VALUES (1, 'Vending Co', 10, '2026-11-01T00:00:00.0000000Z', 'Australia/Sydney');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Receipts" (
                "Id", "BusinessId", "Title", "PurchaseDate", "CreatedAt")
            VALUES (
                1, 1, 'Restock', '2026-11-02T00:00:00.0000000Z', '2026-11-02T00:00:00.0000000Z');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "ReceiptScanUsages" (
                "Id", "BusinessId", "MonthKey", "ScanCount", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES (
                1, 1, 202611, 42, '2026-11-02T00:00:00.0000000Z', '2026-11-11T00:00:00.0000000Z');
            """);

        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(UsageTable, await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Equal(
            1L,
            await ScalarAsync(connection, @"SELECT COUNT(*) FROM ""Receipts"" WHERE ""Title"" = 'Restock'"));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
}
