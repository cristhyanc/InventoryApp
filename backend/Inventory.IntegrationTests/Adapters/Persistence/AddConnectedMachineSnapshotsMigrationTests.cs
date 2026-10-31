using System.Globalization;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddConnectedMachineSnapshots</c> (issue #978): the snapshot table with its
/// daily claim, and the account series on each business's existing Nayax connection.
///
/// <para>Three claims are worth proving. The first is the issue's own migration note:
/// <strong>it is additive and changes no data.</strong> The table arrives empty - nothing is counted
/// by a migration - and a connection that already existed keeps its operator id, its ciphertext, its
/// status, its revision and its last-tested instant exactly as they were. The second is that such a
/// connection becomes <strong>account series 1</strong>, so the first snapshot taken for it belongs to
/// the account it is already on rather than to a series nobody chose. The third is the uniqueness the
/// whole daily claim rests on, read from the schema rather than trusted to the application.</para>
/// </summary>
public class AddConnectedMachineSnapshotsMigrationTests
{
    private const string PreviousMigration = "20261029120000_AllowPurchaseWithoutDocument";

    private const string TargetMigration = "AddConnectedMachineSnapshots";

    private const string SnapshotTable = "BusinessConnectedMachineSnapshots";

    private const string ConnectionTable = "BusinessNayaxConnections";

    private static readonly DateTime Created = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Saved = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_adds_the_table_and_leaves_an_existing_connection_untouched_on_series_1()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(SnapshotTable, await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.DoesNotContain(
                "AccountSeries",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, ConnectionTable));

            before.Businesses.Add(new Business
            {
                Id = 1,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = Created,
            });
            await before.SaveChangesAsync();

            // The connection is inserted over the connection rather than through the EF model: the
            // model describes the schema *after* this migration, so a DbSet write here would name the
            // two columns the previous schema does not have yet. This is the row an existing
            // deployment is upgraded with.
            await InsertConnectionAtPreviousSchemaAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(SnapshotTable, await MigrationSchemaProbe.TableNamesAsync(connection));

            // Nothing is counted by a migration: the evidence table arrives empty.
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, SnapshotTable));

            var stored = await after.BusinessNayaxConnections.AsNoTracking().SingleAsync();

            // The account the business is already on is series 1, and no instant is invented for a
            // change that never happened.
            Assert.Equal(1, stored.AccountSeries);
            Assert.Null(stored.AccountSeriesChangedAtUtc);

            // And every credential fact is exactly as it was.
            Assert.Equal("4242", stored.OperatorId);
            Assert.Equal("ciphertext", stored.AccessTokenCiphertext);
            Assert.Equal("key-1", stored.EncryptionKeyId);
            Assert.Equal(NayaxConnectionStatus.Ready, stored.Status);
            Assert.Equal(3, stored.CredentialRevision);
            Assert.Equal(Saved, stored.LastTestedAtUtc);
            Assert.Equal(Saved, stored.UpdatedAtUtc);
        }
    }

    /// <summary>
    /// The snapshot table's own shape: the business column every tenant-owned table carries, the
    /// evidence columns, and the unique index on (business, UTC date, account series) that is the
    /// daily claim itself.
    /// </summary>
    [Fact]
    public async Task The_snapshot_table_carries_the_business_column_and_the_daily_unique_claim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, SnapshotTable);
        Assert.Contains("BusinessId", columns);
        Assert.Contains("SnapshotDateUtc", columns);
        Assert.Contains("AccountSeries", columns);
        Assert.Contains("MachineCount", columns);
        Assert.Contains("ProviderMachineIds", columns);
        Assert.Contains("TakenAtUtc", columns);
        Assert.Contains("ReadWasComplete", columns);

        var createSql = await MigrationSchemaProbe.CreateTableSqlAsync(connection, SnapshotTable);
        Assert.Contains("\"ProviderMachineIds\" TEXT NOT NULL", createSql, StringComparison.Ordinal);

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, SnapshotTable);
        Assert.Contains($"IX_{SnapshotTable}_BusinessId", indexes);
        Assert.Contains($"IX_{SnapshotTable}_BusinessId_SnapshotDateUtc_AccountSeries", indexes);

        // The claim is unique, which is the whole mechanism: two complete reads of one day cannot both
        // be stored.
        var uniqueSql = await IndexSqlAsync(
            connection,
            $"IX_{SnapshotTable}_BusinessId_SnapshotDateUtc_AccountSeries");
        Assert.Contains("CREATE UNIQUE INDEX", uniqueSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The account series column is added with a database default of 1, so a connection row written
    /// by an older release - or by anything that does not name the column - is on its first account
    /// series rather than on series zero, which is no account at all.
    /// </summary>
    [Fact]
    public async Task The_account_series_column_defaults_to_the_first_series()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        Assert.Equal(
            "1",
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, ConnectionTable, "AccountSeries"));
    }

    /// <summary>
    /// One business's stored Nayax connection, as the schema before this migration held it: the same
    /// values the entity carries, written with the columns that existed then and none that did not.
    /// </summary>
    private static async Task InsertConnectionAtPreviousSchemaAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "BusinessNayaxConnections"
                ("BusinessId", "OperatorId", "AccessTokenCiphertext", "EncryptionKeyId", "Status",
                 "CredentialRevision", "LastTestedAtUtc", "MachineChecksPending", "UpdatedAtUtc")
            VALUES ($businessId, $operatorId, $ciphertext, $keyId, $status, $revision, $tested, 0, $updated);
            """;
        command.Parameters.AddWithValue("$businessId", 1);
        command.Parameters.AddWithValue("$operatorId", "4242");
        command.Parameters.AddWithValue("$ciphertext", "ciphertext");
        command.Parameters.AddWithValue("$keyId", "key-1");
        command.Parameters.AddWithValue("$status", (int)NayaxConnectionStatus.Ready);
        command.Parameters.AddWithValue("$revision", 3);
        // The text form the SQLite provider itself persists a DateTime in, so the row reads back as
        // the instant it was written with.
        var saved = Saved.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$tested", saved);
        command.Parameters.AddWithValue("$updated", saved);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> IndexSqlAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", index);

        return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
    }
}
