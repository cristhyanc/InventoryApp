using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessLifecycleStatusAndProfile</c> (issue #540).
///
/// This migration is the one in this slice that can change who may reach a business's data, so the
/// mapping is what has to be proved on a real relational database: a business that was active
/// becomes <c>Active</c> and keeps resolving, a business that was deactivated becomes
/// <c>Deactivated</c> and keeps being denied, the flag the decision used to be made from is gone
/// afterwards, and the profile columns arrive holding nothing at all.
/// </summary>
public class BusinessLifecycleStatusMigrationTests
{
    private const string PreviousMigration = "20261011024616_AddMemberDisplayNameAndEmail";
    private const string StatusMigration = "20261011120000_AddBusinessLifecycleStatusAndProfile";

    private const string CreatedAtUtc = "2026-01-01 03:04:05";

    /// <summary><c>BusinessStatus.Active</c>'s stored value, as the migration writes it.</summary>
    private const long ActiveStatus = 20;

    /// <summary><c>BusinessStatus.Deactivated</c>'s stored value.</summary>
    private const long DeactivatedStatus = 40;

    private static readonly string[] ProfileColumns =
    [
        "Abn", "AppliedAtUtc", "ContactEmail", "DecidedAtUtc", "GstRegistered", "RejectionReason",
    ];

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

    /// <summary>
    /// The mapping both ways, on one database, with the rest of each row untouched: the active
    /// business becomes <c>Active</c>, the deactivated one becomes <c>Deactivated</c>, and the
    /// flag they were derived from no longer exists for anything to read.
    /// </summary>
    [Fact]
    public async Task The_migration_maps_the_active_flag_to_the_lifecycle_status_and_removes_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses");
        Assert.Contains("IsActive", columnsBefore);
        Assert.DoesNotContain("Status", columnsBefore);

        // Seeded with raw SQL, not the EF model: the model already describes columns this older
        // schema has not created yet, and the flag this one carries no longer exists in it.
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (2, 'Offboarded Vending Co', 0, '{CreatedAtUtc}', 'Australia/Hobart');
            """);

        await MigrateToAsync(db, StatusMigration);

        var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses");
        Assert.Contains("Status", columnsAfter);
        Assert.DoesNotContain("IsActive", columnsAfter);

        Assert.Equal(ActiveStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 1;"));
        Assert.Equal(DeactivatedStatus, await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 2;"));

        // Everything else about both rows comes through exactly as it was stored.
        Assert.Equal("Vending Co", await ScalarAsync(connection, "SELECT Name FROM Businesses WHERE Id = 1;"));
        Assert.Equal(
            "Australia/Sydney",
            await ScalarAsync(connection, "SELECT TimeZoneId FROM Businesses WHERE Id = 1;"));
        Assert.Equal(
            "Australia/Hobart",
            await ScalarAsync(connection, "SELECT TimeZoneId FROM Businesses WHERE Id = 2;"));
        Assert.Equal(CreatedAtUtc, await ScalarAsync(connection, "SELECT CreatedAtUtc FROM Businesses WHERE Id = 1;"));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
    }

    /// <summary>
    /// The profile columns arrive empty. Nothing is derived for the business that predates the
    /// application form: no contact address copied from a member, no assumed GST registration, no
    /// ABN, and no application or decision instant invented from <c>CreatedAtUtc</c>.
    /// </summary>
    [Fact]
    public async Task The_profile_columns_arrive_holding_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}', 'Australia/Sydney');
            """);

        await MigrateToAsync(db, StatusMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses");
        foreach (var column in ProfileColumns)
        {
            Assert.Contains(column, columns);
            Assert.Null(await ScalarAsync(connection, $"SELECT {column} FROM Businesses WHERE Id = 1;"));
        }
    }

    /// <summary>
    /// A database with no business yet - the state a fresh deployment migrates from - upgrades the
    /// same way and invents no business to map.
    /// </summary>
    [Fact]
    public async Task The_migration_invents_no_business_when_there_is_none_to_map()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, StatusMigration);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
        Assert.DoesNotContain("IsActive", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));
    }

    /// <summary>
    /// The upgraded rows are what the access decision is then made from, through the real adapter
    /// and the real policy: the business that was active resolves for its member, and the one that
    /// was deactivated is denied. This is the migration's actual claim - that nobody's access
    /// changed across it - rather than a statement about one column's value.
    /// </summary>
    [Fact]
    public async Task Access_after_the_upgrade_is_decided_from_the_mapped_status()
    {
        const string tid = "11111111-1111-1111-1111-111111111111";
        const string activeOid = "22222222-2222-2222-2222-222222222222";
        const string deactivatedOid = "33333333-3333-3333-3333-333333333333";

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (2, 'Offboarded Vending Co', 0, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO BusinessMemberships
                (BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (1, '{tid}', '{activeOid}', 40, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships
                (BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (2, '{tid}', '{deactivatedOid}', 40, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            """);

        await MigrateToAsync(db, StatusMigration);

        var store = new Inventory.Infrastructure.Persistence.EfBusinessMembershipStore(db);

        Assert.True(ActorIdentity.TryCreate(tid, activeOid, out var activeActor));
        var resolved = BusinessMembershipResolutionPolicy.Resolve(
            await store.FindMembershipsAsync(activeActor!, CancellationToken.None));
        Assert.True(resolved.IsResolved);
        Assert.Equal(BusinessId.From(1), resolved.ResolvedBusinessId);

        Assert.True(ActorIdentity.TryCreate(tid, deactivatedOid, out var deactivatedActor));
        var denied = BusinessMembershipResolutionPolicy.Resolve(
            await store.FindMembershipsAsync(deactivatedActor!, CancellationToken.None));
        Assert.False(denied.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.BusinessInactive, denied.DenialReason);
    }
}
