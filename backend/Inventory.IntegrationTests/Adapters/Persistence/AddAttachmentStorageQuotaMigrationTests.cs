using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddAttachmentStorageQuota</c> (issue #968): the reservations table and the
/// expense attachment version arrive, every recorded expense and purchase survives the upgrade
/// untouched, and rolling back takes exactly those two additions away again.
///
/// <para>Both additions are deliberately additive. Nothing is backfilled: an expense that already
/// existed starts at version <c>0</c>, which is what the column's default gives it, and no
/// attachment's recorded size, stored name or content type is read or rewritten. That is the part a
/// reviewer cannot take on trust, because a migration that touched an attachment's metadata would
/// be a migration that could mis-charge a business for its own receipts.</para>
///
/// <para>Relational SQLite rather than InMemory: a created table, an added column with a default,
/// an index and preserved rows across an upgrade are relational facts.</para>
/// </summary>
public class AddAttachmentStorageQuotaMigrationTests
{
    private const string PreviousMigration = "20261029180000_AddConnectedMachineSnapshots";
    private const string TargetMigration = "AddAttachmentStorageQuota";
    private const string ReservationsTable = "AttachmentStorageReservations";

    [Fact]
    public async Task The_upgrade_adds_the_reservations_table_its_index_and_the_attachment_version()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(ReservationsTable, await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.DoesNotContain(
            "AttachmentVersion",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "OperatingExpenses"));

        await MigrateToAsync(db, TargetMigration);

        Assert.Contains(ReservationsTable, await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Contains(
            "AttachmentVersion",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "OperatingExpenses"));

        // Every column the accounting needs, and nothing about a document's contents: no file name,
        // no content type, no supplier, no amount.
        Assert.Equal(
            [
                "BusinessId",
                "Category",
                "ChargeableBytes",
                "CreatedAtUtc",
                "ExpiresAtUtc",
                "Id",
                "LimitBytes",
                "RecordId",
                "RecordVersion",
                "UploadBytes",
            ],
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, ReservationsTable))
                .Order(StringComparer.Ordinal));

        // The index every statement over this table uses: one business's unexpired reservations.
        Assert.Contains(
            $"IX_{ReservationsTable}_BusinessId_ExpiresAtUtc",
            await MigrationSchemaProbe.IndexNamesAsync(connection, ReservationsTable));
    }

    /// <summary>
    /// The data half: an expense with an attachment and a purchase with a document, recorded under
    /// the previous schema, come through the upgrade with every value they had - and the expense
    /// starts at attachment version <c>0</c> rather than at anything inferred from its attachment.
    /// </summary>
    [Fact]
    public async Task Recorded_attachments_survive_the_upgrade_unchanged_and_start_at_version_zero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Businesses" ("Id", "Name", "Status", "CreatedAtUtc", "TimeZoneId")
            VALUES (1, 'Vending Co', 10, '2026-10-01T00:00:00.0000000Z', 'Australia/Sydney');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Receipts" (
                "Id", "BusinessId", "Title", "PurchaseDate", "CreatedAt",
                "FileName", "StoredFileName", "ContentType", "FileSizeBytes")
            VALUES (
                1, 1, 'Restock', '2026-10-02T00:00:00.0000000Z', '2026-10-02T00:00:00.0000000Z',
                'receipt.jpg', 'stored-receipt.jpg', 'image/jpeg', 3456);
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "OperatingExpenses" (
                "Id", "BusinessId", "ExpenseDate", "Category", "Description",
                "AmountExGst", "GstAmount", "TotalAmount", "CreatedAt", "UpdatedAt",
                "AttachmentFileName", "AttachmentStoredFileName", "AttachmentContentType",
                "AttachmentFileSizeBytes")
            VALUES (
                1, 1, '2026-10-03T00:00:00.0000000Z', 1, 'Cover',
                10, 1, 11, '2026-10-03T00:00:00.0000000Z', '2026-10-03T00:00:00.0000000Z',
                'invoice.pdf', 'stored-invoice.pdf', 'application/pdf', 7890);
            """);

        await MigrateToAsync(db, TargetMigration);

        await using var verify = TestAppDbContext.Unrestricted(OptionsFor(connection));
        var purchase = await verify.Receipts.AsNoTracking().SingleAsync();
        Assert.Equal(
            ("receipt.jpg", "stored-receipt.jpg", "image/jpeg", 3456L),
            (purchase.FileName, purchase.StoredFileName, purchase.ContentType, purchase.FileSizeBytes));

        var expense = await verify.OperatingExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(
            ("invoice.pdf", "stored-invoice.pdf", "application/pdf", 7890L),
            (expense.AttachmentFileName, expense.AttachmentStoredFileName,
                expense.AttachmentContentType, expense.AttachmentFileSizeBytes));
        Assert.Equal(0, expense.AttachmentVersion);

        // And nothing has reserved anything: the accounting starts empty, so no business is charged
        // for an upload that never happened.
        Assert.Empty(await verify.AttachmentStorageReservations.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Rolling back removes exactly the two additions and leaves the recorded expense - including
    /// its attachment - in place. The quota is accounting about uploads in progress, so there is
    /// nothing in it a rollback has to preserve.
    /// </summary>
    [Fact]
    public async Task Rolling_back_removes_both_additions_and_keeps_the_records()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Businesses" ("Id", "Name", "Status", "CreatedAtUtc", "TimeZoneId")
            VALUES (1, 'Vending Co', 10, '2026-10-01T00:00:00.0000000Z', 'Australia/Sydney');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "OperatingExpenses" (
                "Id", "BusinessId", "ExpenseDate", "Category", "Description",
                "AmountExGst", "GstAmount", "TotalAmount", "CreatedAt", "UpdatedAt",
                "AttachmentFileName", "AttachmentStoredFileName", "AttachmentContentType",
                "AttachmentFileSizeBytes", "AttachmentVersion")
            VALUES (
                1, 1, '2026-10-03T00:00:00.0000000Z', 1, 'Cover',
                10, 1, 11, '2026-10-03T00:00:00.0000000Z', '2026-10-03T00:00:00.0000000Z',
                'invoice.pdf', 'stored-invoice.pdf', 'application/pdf', 7890, 2);
            """);

        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(ReservationsTable, await MigrationSchemaProbe.TableNamesAsync(connection));
        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "OperatingExpenses");
        Assert.DoesNotContain("AttachmentVersion", columns);
        Assert.Contains("AttachmentStoredFileName", columns);

        Assert.Equal(
            1L,
            await ScalarAsync(connection, @"SELECT COUNT(*) FROM ""OperatingExpenses"" WHERE ""AttachmentFileSizeBytes"" = 7890"));
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
