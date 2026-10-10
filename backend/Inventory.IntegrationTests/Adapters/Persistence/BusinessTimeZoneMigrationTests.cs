using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessTimeZone</c> (issue #499).
///
/// The migration has to be additive in the strict sense: it gives every business a time-zone
/// column and backfills the existing row with <c>Australia/Sydney</c> - the zone this application
/// has always reported in - and it changes nothing else. In particular it rewrites no stored
/// instant: the upgrade changes which zone business dates are *derived* in, not what any recorded
/// timestamp means, so a sale that happened at a given instant still falls on the same business
/// day as before.
/// </summary>
public class BusinessTimeZoneMigrationTests
{
    private const string PreviousMigration = "20261008124919_AddNayaxSaleTimestampRepairs";
    private const string TimeZoneMigration = "20261010025919_AddBusinessTimeZone";
    private const string CreatedAtUtc = "2026-01-01 03:04:05";

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

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

    [Fact]
    public async Task The_migration_adds_the_column_and_backfills_the_existing_business_with_Sydney()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(
            "TimeZoneId",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

        // Seeded with raw SQL, not the EF model: the model already knows about a column this
        // older schema has not created yet.
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}');
            """);

        await MigrateToAsync(db, TimeZoneMigration);

        Assert.Contains(
            "TimeZoneId",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

        // The existing business keeps working exactly as it did, in the zone it always used, and
        // every other column of the row it was already stored with - its creation instant
        // included - comes through untouched.
        Assert.Equal(
            "Australia/Sydney",
            await ScalarAsync(connection, "SELECT TimeZoneId FROM Businesses WHERE Id = 1;"));
        Assert.Equal("Vending Co", await ScalarAsync(connection, "SELECT Name FROM Businesses WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT IsActive FROM Businesses WHERE Id = 1;"));
        Assert.Equal(CreatedAtUtc, await ScalarAsync(connection, "SELECT CreatedAtUtc FROM Businesses WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
    }

    /// <summary>
    /// A database with no business yet - the state a fresh deployment migrates from - upgrades the
    /// same way and invents no business to backfill.
    /// </summary>
    [Fact]
    public async Task The_migration_invents_no_business_when_there_is_none_to_backfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, TimeZoneMigration);

        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
    }
}
