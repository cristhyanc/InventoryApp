using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxConnectionTestDrafts</c> (issue #536).
///
/// The migration lands beside the table that holds each business's real Nayax credentials, so
/// "purely additive" has to be proved rather than asserted: the new table arrives with the columns
/// the model describes and a <em>unique</em> ownership index, no other table gains or loses a column
/// or an index, every existing row keeps every value it had - the stored connection's ciphertext, key
/// id, status and revision included - and nothing is backfilled into the new table.
///
/// Relational SQLite rather than InMemory, because a created table, a unique index and "no existing
/// table was rebuilt" are relational facts.
/// </summary>
public class AddNayaxConnectionTestDraftsMigrationTests
{
    private const string PreviousMigration = "20261011120000_AddBusinessLifecycleStatusAndProfile";
    private const string DraftsMigration = "AddNayaxConnectionTestDrafts";

    private const string DraftTable = "NayaxConnectionTestDrafts";
    private const string DraftIndex = "IX_NayaxConnectionTestDrafts_BusinessId";

    private const string Ciphertext = "Y2lwaGVydGV4dC1ub3QtYS1yZWFsLXRva2Vu";
    private const string KeyId = "2026-10";
    private const string CreatedAtUtc = "2026-01-01 03:04:05";
    private const string EmptyJson = "{}";

    [Fact]
    public async Task The_migration_creates_the_draft_table_with_a_unique_ownership_index()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        Assert.DoesNotContain(DraftTable, await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, DraftsMigration);

        Assert.Contains(DraftTable, await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Equal(
            new[]
            {
                "Id",
                "BusinessId",
                "OperatorId",
                "AccessTokenCiphertext",
                "EncryptionKeyId",
                "ResultsJson",
                "CreatedByDirectoryTenantId",
                "CreatedByObjectId",
                "CreatedAt",
                "ExpiresAt",
            },
            await MigrationSchemaProbe.ColumnNamesAsync(connection, DraftTable));
        Assert.Contains(DraftIndex, await MigrationSchemaProbe.IndexNamesAsync(connection, DraftTable));
        Assert.Equal("1", await ScalarAsync(
            connection,
            $"SELECT \"unique\" FROM pragma_index_list('{DraftTable}') WHERE name = '{DraftIndex}';"));
    }

    /// <summary>
    /// A second draft for one business cannot be inserted, which is what makes "a new test replaces
    /// the previous draft" a schema guarantee rather than something the adapter has to remember.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_hold_two_drafts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, DraftsMigration);
        await ExecuteAsync(connection, InsertDraft("11111111-1111-1111-1111-111111111111"));

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(connection, InsertDraft("22222222-2222-2222-2222-222222222222")));

        Assert.Contains("UNIQUE", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stored Nayax connection is the record this new table sits next to and must not disturb:
    /// its ciphertext, key id, status, revision and instants are byte-for-byte what they were, and
    /// the table itself was not rebuilt.
    /// </summary>
    [Fact]
    public async Task The_migration_leaves_the_stored_nayax_connection_exactly_as_it_was()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);

        var connectionsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxConnections");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessNayaxConnections");
        var rowBefore = await StoredConnectionAsync(connection);

        await MigrateToAsync(db, DraftsMigration);

        Assert.Equal(
            connectionsBefore,
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxConnections"));
        Assert.Equal(
            indexesBefore,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessNayaxConnections"));
        Assert.Equal(rowBefore, await StoredConnectionAsync(connection));
    }

    /// <summary>
    /// Nothing is backfilled. A business that has never tested a token has no draft, which is the
    /// state every business starts in: a migration must never invent a credential holder.
    /// </summary>
    [Fact]
    public async Task The_migration_creates_no_draft_for_an_existing_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);

        await MigrateToAsync(db, DraftsMigration);

        Assert.Equal("0", await ScalarAsync(connection, $"SELECT COUNT(*) FROM {DraftTable};"));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>
    /// Seeded with raw SQL rather than the EF model, because the model already knows about a table
    /// the older schema has not created yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 20, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO BusinessNayaxConnections
                (Id, BusinessId, OperatorId, AccessTokenCiphertext, EncryptionKeyId, Status,
                 CredentialRevision, LastTestedAtUtc, UpdatedAtUtc)
                VALUES (1, 1, '2002736764', '{Ciphertext}', '{KeyId}', 2, 3, '{CreatedAtUtc}', '{CreatedAtUtc}');
            """);

    private static string InsertDraft(string draftId) => $"""
        INSERT INTO {DraftTable}
            (Id, BusinessId, OperatorId, AccessTokenCiphertext, EncryptionKeyId, ResultsJson,
             CreatedByDirectoryTenantId, CreatedByObjectId, CreatedAt, ExpiresAt)
            VALUES ('{draftId}', 1, '2002736764', '{Ciphertext}', '{KeyId}', '{EmptyJson}',
                    '99999999-9999-9999-9999-999999999999', '88888888-8888-8888-8888-888888888888',
                    '{CreatedAtUtc}', '{CreatedAtUtc}');
        """;

    private static Task<string?> StoredConnectionAsync(SqliteConnection connection) =>
        ScalarAsync(
            connection,
            "SELECT Id || '|' || BusinessId || '|' || OperatorId || '|' || AccessTokenCiphertext || '|' || "
                + "EncryptionKeyId || '|' || Status || '|' || CredentialRevision || '|' || "
                + "LastTestedAtUtc || '|' || UpdatedAtUtc FROM BusinessNayaxConnections;");

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
