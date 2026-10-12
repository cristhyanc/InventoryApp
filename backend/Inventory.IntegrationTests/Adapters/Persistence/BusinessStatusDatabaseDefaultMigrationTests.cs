using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RemoveBusinessStatusActiveDatabaseDefault</c> (issue #584).
///
/// The migration's whole purpose is a value nobody can see in a row: what the <c>Status</c> column
/// gives an <c>INSERT</c> that states no status. So the schema is read back from SQLite itself
/// before and after, and the thing that must *not* change - every existing business's exact status,
/// and therefore its access - is proved on the same database, through the real adapter and the real
/// policy.
/// </summary>
public class BusinessStatusDatabaseDefaultMigrationTests
{
    private const string PreviousMigration = "20261011150000_AddNayaxConnectionTestDrafts";
    private const string DefaultMigration = "20261012005341_RemoveBusinessStatusActiveDatabaseDefault";

    private const string CreatedAtUtc = "2026-01-01 03:04:05";

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string ActiveOid = "22222222-2222-2222-2222-222222222222";
    private const string DeactivatedOid = "33333333-3333-3333-3333-333333333333";

    /// <summary>The stored statuses, as the rows carry them and the migration must leave them.</summary>
    private const long PendingStatus = 10;
    private const long ActiveStatus = 20;
    private const long RejectedStatus = 30;
    private const long DeactivatedStatus = 40;

    /// <summary>The default after the upgrade: a value no status is declared at.</summary>
    private const long UndeclaredStatus = 0;

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

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
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>One business per declared status, so every stored value is covered by the upgrade.</summary>
    private static Task SeedEveryStatusAsync(SqliteConnection connection) =>
        ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Trading Vending Co', {ActiveStatus}, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (2, 'Offboarded Vending Co', {DeactivatedStatus}, '{CreatedAtUtc}', 'Australia/Hobart');
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (3, 'Applicant Vending Co', {PendingStatus}, '{CreatedAtUtc}', 'Australia/Perth');
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (4, 'Refused Vending Co', {RejectedStatus}, '{CreatedAtUtc}', 'Australia/Darwin');
            """);

    private static Task<string?> StatusDefaultAsync(SqliteConnection connection) =>
        MigrationSchemaProbe.ColumnDefaultAsync(connection, "Businesses", "Status");

    /// <summary>
    /// The one thing that changes is the default, and the schema says so both before and after: the
    /// column arrives defaulting to <c>Active</c> and leaves defaulting to a value that is not a
    /// status, while every row keeps the status it had and the table gains no leftover column.
    /// </summary>
    [Fact]
    public async Task The_migration_replaces_the_active_default_and_preserves_every_status()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        Assert.Equal(ActiveStatus.ToString(), await StatusDefaultAsync(connection));

        await SeedEveryStatusAsync(connection);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses");

        await MigrateToAsync(db, DefaultMigration);

        Assert.Equal(UndeclaredStatus.ToString(), await StatusDefaultAsync(connection));

        Assert.Equal(ActiveStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 1;"));
        Assert.Equal(DeactivatedStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 2;"));
        Assert.Equal(PendingStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 3;"));
        Assert.Equal(RejectedStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 4;"));
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));

        // The column moves to the end of the table, which nothing reads, and the column the status
        // travelled through is gone rather than left behind as a second copy of the access decision.
        var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses");
        Assert.Equal(columnsBefore.OrderBy(name => name), columnsAfter.OrderBy(name => name));
        Assert.DoesNotContain("StatusWithoutActiveDefault", columnsAfter);

        // And the rest of each row came through the copy untouched.
        Assert.Equal("Trading Vending Co", await ScalarAsync(connection, "SELECT Name FROM Businesses WHERE Id = 1;"));
        Assert.Equal(
            "Australia/Hobart",
            await ScalarAsync(connection, "SELECT TimeZoneId FROM Businesses WHERE Id = 2;"));
        Assert.Equal(CreatedAtUtc, await ScalarAsync(connection, "SELECT CreatedAtUtc FROM Businesses WHERE Id = 1;"));
        Assert.Null(await ScalarAsync(connection, "SELECT Abn FROM Businesses WHERE Id = 1;"));
    }

    /// <summary>
    /// After the upgrade, an <c>INSERT</c> that omits the column - the case the whole migration
    /// exists for - produces a business that is not active.
    /// </summary>
    [Fact]
    public async Task After_the_upgrade_an_insert_without_a_status_is_not_active()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, DefaultMigration);

        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId)
                VALUES (9, 'Nobody stated a status', '{CreatedAtUtc}', 'Australia/Sydney');
            """);

        var stored = (long)(await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 9;"))!;

        Assert.Equal(UndeclaredStatus, stored);
        Assert.NotEqual(ActiveStatus, stored);
        Assert.False(BusinessStatuses.IsSupported((BusinessStatus)stored));
    }

    /// <summary>
    /// A database with no business yet - the state a fresh deployment upgrades from - upgrades the
    /// same way and invents no row to copy.
    /// </summary>
    [Fact]
    public async Task The_migration_invents_no_business_when_there_is_none_to_copy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, DefaultMigration);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
        Assert.Equal(UndeclaredStatus.ToString(), await StatusDefaultAsync(connection));
    }

    /// <summary>
    /// The migration's actual claim: nobody's access changes across it. The business that was
    /// trading still resolves for its member afterwards, and the offboarded one is still denied -
    /// decided through the real adapter and the real policy, not by reading a column.
    /// </summary>
    [Fact]
    public async Task Access_across_the_upgrade_is_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        await SeedEveryStatusAsync(connection);
        await ExecuteAsync(connection, $"""
            INSERT INTO BusinessMemberships
                (BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (1, '{Tid}', '{ActiveOid}', 40, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships
                (BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (2, '{Tid}', '{DeactivatedOid}', 40, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            """);

        await MigrateToAsync(db, DefaultMigration);

        var store = new Inventory.Infrastructure.Persistence.EfBusinessMembershipStore(db);

        Assert.True(ActorIdentity.TryCreate(Tid, ActiveOid, out var activeActor));
        var resolved = BusinessMembershipResolutionPolicy.Resolve(
            await store.FindMembershipsAsync(activeActor!, CancellationToken.None));
        Assert.True(resolved.IsResolved);
        Assert.Equal(BusinessId.From(1), resolved.ResolvedBusinessId);

        Assert.True(ActorIdentity.TryCreate(Tid, DeactivatedOid, out var deactivatedActor));
        var denied = BusinessMembershipResolutionPolicy.Resolve(
            await store.FindMembershipsAsync(deactivatedActor!, CancellationToken.None));
        Assert.False(denied.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.BusinessInactive, denied.DenialReason);
    }

    /// <summary>
    /// <c>Down</c> returns the schema issue #540 left - including, deliberately, its
    /// <c>Active</c> default - and preserves every status on the way back, so a rollback changes no
    /// business's access either.
    /// </summary>
    [Fact]
    public async Task The_downgrade_restores_the_previous_default_and_preserves_every_status()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedEveryStatusAsync(connection);
        await MigrateToAsync(db, DefaultMigration);

        await MigrateToAsync(db, PreviousMigration);

        Assert.Equal(ActiveStatus.ToString(), await StatusDefaultAsync(connection));

        Assert.Equal(ActiveStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 1;"));
        Assert.Equal(DeactivatedStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 2;"));
        Assert.Equal(PendingStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 3;"));
        Assert.Equal(RejectedStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 4;"));
        Assert.DoesNotContain(
            "StatusWithoutActiveDefault",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));
    }
}
