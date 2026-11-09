using Inventory.Application.PlatformOptions;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPlatformMenuVisibility</c> (issue #1148).
///
/// <para>The acceptance criterion this file exists for is the one a reviewer cannot take on trust:
/// an installation that had switched Historical GST Classification <strong>off</strong> keeps it off
/// under the new key, and an installation that had switched it on - or had never switched anything -
/// gets <strong>no row at all</strong>, because a missing row means On and inserting one would be
/// storing a decision nobody made. The old <c>PlatformOptionVisibility</c> table is left exactly as
/// it was in every case.</para>
///
/// <para>Relational SQLite rather than InMemory, because a conditional <c>INSERT ... SELECT</c>, a
/// string primary key and "nothing else was touched" are relational facts.</para>
/// </summary>
public class AddPlatformMenuVisibilityMigrationTests
{
    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261108070703_AddMasterProductSupplierSearchTerms";

    private const string TargetMigration = "AddPlatformMenuVisibility";

    private const string Table = "PlatformMenuVisibility";

    private const string GstKey = MenuVisibilityKeys.HistoricalGstClassification;

    /// <summary>
    /// The criterion in full, in the direction that matters: a stored <c>false</c> is carried over
    /// as one row saying that destination is off, with the old row's own instant rather than an
    /// invented one - and that one row is all the table holds.
    /// </summary>
    [Fact]
    public async Task A_stored_gst_off_is_carried_over_as_the_one_switched_off_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await MigratedToPreviousAsync(connection);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO PlatformOptionVisibility (Id, HistoricalGstClassificationVisible, UpdatedAtUtc)
                VALUES (1, 0, '2026-11-01 02:03:04');
            """);

        Assert.DoesNotContain(Table, await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateAsync(options, TargetMigration);

        Assert.Contains(Table, await MigrationSchemaProbe.TableNamesAsync(connection));
        var rows = await StoredRowsAsync(connection);
        var row = Assert.Single(rows);
        Assert.Equal(GstKey, row.Key);
        Assert.False(row.Visible);
        Assert.Equal("2026-11-01 02:03:04", row.UpdatedAtUtc);

        // And the superseded row is left exactly as it was: not dropped, not edited, not deleted.
        Assert.Equal(
            0L,
            Convert.ToInt64(
                await ScalarAsync(
                    connection,
                    "SELECT HistoricalGstClassificationVisible FROM PlatformOptionVisibility WHERE Id = 1;"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A stored <c>true</c>, and no row at all, both carry over <em>nothing</em>: the new table is
    /// empty, every destination reads as On, and the visibility an installation has today is exactly
    /// the visibility it keeps. Storing a row for "on" would also be storing an instant nobody
    /// switched anything at.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO PlatformOptionVisibility (Id, HistoricalGstClassificationVisible, UpdatedAtUtc) VALUES (1, 1, '2026-11-01 02:03:04');")]
    [InlineData(null)]
    public async Task A_stored_gst_on_or_no_row_at_all_carries_over_nothing(string? seed)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await MigratedToPreviousAsync(connection);
        if (seed is not null)
        {
            await ExecuteAsync(connection, seed);
        }

        await MigrateAsync(options, TargetMigration);

        Assert.Empty(await StoredRowsAsync(connection));
    }

    /// <summary>
    /// The migration adds one table and touches nothing else in the schema - no column, no index and
    /// no other table, including the one it supersedes.
    /// </summary>
    [Fact]
    public async Task The_migration_adds_one_table_and_changes_no_other()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await MigratedToPreviousAsync(connection);
        var tablesBefore = await MigrationSchemaProbe.TableNamesAsync(connection);
        var optionColumnsBefore =
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "PlatformOptionVisibility");

        await MigrateAsync(options, TargetMigration);

        Assert.Equal(
            tablesBefore.Append(Table).Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.TableNamesAsync(connection)).Order(StringComparer.Ordinal));
        Assert.Equal(
            optionColumnsBefore,
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "PlatformOptionVisibility"));
    }

    /// <summary>
    /// The table is about no business, which is the half of the tenancy decision a schema can carry:
    /// there is no <c>BusinessId</c> column for anything to filter or stamp, and the key is the
    /// destination's own catalogue key rather than anything the database assigns - which is what
    /// makes a second write to one destination an update and two writes to two destinations two
    /// independent rows.
    /// </summary>
    [Fact]
    public async Task The_table_carries_no_business_column_and_a_key_the_database_does_not_assign()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await MigratedToPreviousAsync(connection);
        await MigrateAsync(options, TargetMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, Table);

        Assert.DoesNotContain("BusinessId", columns);
        Assert.Equal(["Key", "UpdatedAtUtc", "Visible"], columns.Order(StringComparer.Ordinal).ToArray());

        var create = await MigrationSchemaProbe.CreateTableSqlAsync(connection, Table);
        Assert.Contains("PRIMARY KEY", create, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOINCREMENT", create, StringComparison.Ordinal);
    }

    /// <summary>
    /// The primary key refuses a second row for the same destination, whatever a pre-check saw: that
    /// is what makes one destination one answer rather than two rows a later read would have to
    /// choose between.
    /// </summary>
    [Fact]
    public async Task The_primary_key_refuses_a_second_row_for_the_same_destination()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await MigratedToPreviousAsync(connection);
        await MigrateAsync(options, TargetMigration);

        await ExecuteAsync(
            connection,
            $"""INSERT INTO {Table} ("Key", Visible, UpdatedAtUtc) VALUES ('{GstKey}', 0, '2026-11-09 00:00:00');""");

        var error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            $"""INSERT INTO {Table} ("Key", Visible, UpdatedAtUtc) VALUES ('{GstKey}', 1, '2026-11-09 01:00:00');"""));

        Assert.Contains("PlatformMenuVisibility.Key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reversing the migration drops the table and leaves the superseded row untouched - so the GST
    /// answer is not lost at all, and re-applying the migration carries it over again.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_table_and_keeps_the_superseded_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await MigratedToPreviousAsync(connection);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO PlatformOptionVisibility (Id, HistoricalGstClassificationVisible, UpdatedAtUtc)
                VALUES (1, 0, '2026-11-01 02:03:04');
            """);
        await MigrateAsync(options, TargetMigration);
        Assert.Single(await StoredRowsAsync(connection));

        await MigrateAsync(options, PreviousMigration);

        Assert.DoesNotContain(Table, await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateAsync(options, TargetMigration);

        Assert.False(Assert.Single(await StoredRowsAsync(connection)).Visible);
    }

    private static async Task<DbContextOptions<AppDbContext>> MigratedToPreviousAsync(
        SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);
        return options;
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options, string target)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    /// <summary>
    /// The stored rows read with SQL rather than through the entity, for the reason every migration
    /// test here reads with SQL: the test stands on one point in the schema's history.
    /// </summary>
    private static async Task<List<StoredRow>> StoredRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT "Key", Visible, UpdatedAtUtc FROM {Table} ORDER BY "Key";""";
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<StoredRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredRow(reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed record StoredRow(string Key, bool Visible, string UpdatedAtUtc);
}
