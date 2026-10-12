using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxConnectionMachineChecksPending</c> (issue #537).
///
/// The column lands on the table that holds each business's real Nayax credentials, so "purely
/// additive" has to be proved rather than asserted: the column arrives, every other column and index
/// of that table is untouched, every existing row keeps its ciphertext, key id, status, revision and
/// instants byte-for-byte, and the existing rows take the <c>false</c> default rather than being
/// backfilled to "pending".
///
/// That default is the decision worth pinning. A connection no check has ever run against - the one
/// the human-run <c>migrate-nayax-connection</c> command stored, which has been authenticating to
/// Nayax in production - reports "not pending", because the flag exists to ask an Owner to re-test
/// once their machines are set up, not to make a working connection look incomplete.
///
/// Relational SQLite rather than InMemory, because an added column, a preserved index and a
/// defaulted value are relational facts.
/// </summary>
public class AddNayaxConnectionMachineChecksPendingMigrationTests
{
    private const string PreviousMigration = "20261011150000_AddNayaxConnectionTestDrafts";
    private const string MachineChecksMigration = "AddNayaxConnectionMachineChecksPending";

    private const string ConnectionTable = "BusinessNayaxConnections";
    private const string Column = "MachineChecksPending";

    private const string Ciphertext = "Y2lwaGVydGV4dC1ub3QtYS1yZWFsLXRva2Vu";
    private const string KeyId = "2026-10";
    private const string CreatedAtUtc = "2026-01-01 03:04:05";

    [Fact]
    public async Task The_migration_adds_the_column_and_leaves_every_other_column_and_index_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, ConnectionTable);
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, ConnectionTable);
        Assert.DoesNotContain(Column, columnsBefore);

        await MigrateToAsync(db, MachineChecksMigration);

        var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, ConnectionTable);
        Assert.Contains(Column, columnsAfter);
        Assert.Equal(new List<string>(columnsBefore) { Column }, columnsAfter);
        Assert.Equal(
            indexesBefore,
            await MigrationSchemaProbe.IndexNamesAsync(connection, ConnectionTable));
    }

    /// <summary>
    /// The existing connection keeps every value it had, and its new column reads <c>0</c>: nothing
    /// is backfilled, and no credential, status or revision is read or rewritten.
    /// </summary>
    [Fact]
    public async Task An_existing_connection_is_preserved_and_reports_no_pending_machine_checks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);
        var rowBefore = await StoredConnectionAsync(connection);

        await MigrateToAsync(db, MachineChecksMigration);

        Assert.Equal(rowBefore, await StoredConnectionAsync(connection));
        Assert.Equal(
            "0",
            await ScalarAsync(connection, $"SELECT {Column} FROM {ConnectionTable} WHERE BusinessId = 1;"));
    }

    /// <summary>
    /// The draft table the previous migration added is not touched either, and no draft is created:
    /// this migration adds one column and does nothing else at all.
    /// </summary>
    [Fact]
    public async Task The_draft_table_is_untouched_and_no_draft_is_created()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);
        var draftColumnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(
            connection, "NayaxConnectionTestDrafts");

        await MigrateToAsync(db, MachineChecksMigration);

        Assert.Equal(
            draftColumnsBefore,
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxConnectionTestDrafts"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM NayaxConnectionTestDrafts;"));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>
    /// Seeded with raw SQL rather than the EF model, because the model already knows about a column
    /// the older schema has not added yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 20, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO {ConnectionTable}
                (Id, BusinessId, OperatorId, AccessTokenCiphertext, EncryptionKeyId, Status,
                 CredentialRevision, LastTestedAtUtc, UpdatedAtUtc)
                VALUES (1, 1, '2002736764', '{Ciphertext}', '{KeyId}', 2, 3, '{CreatedAtUtc}', '{CreatedAtUtc}');
            """);

    private static Task<string?> StoredConnectionAsync(SqliteConnection connection) =>
        ScalarAsync(
            connection,
            "SELECT Id || '|' || BusinessId || '|' || OperatorId || '|' || AccessTokenCiphertext || '|' || "
                + "EncryptionKeyId || '|' || Status || '|' || CredentialRevision || '|' || "
                + $"LastTestedAtUtc || '|' || UpdatedAtUtc FROM {ConnectionTable};");

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}
