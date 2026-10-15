using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RemoveBusinessMembershipRoleOwnerDatabaseDefault</c> (issue #679), on
/// <c>BusinessStatusDatabaseDefaultMigrationTests</c>'s pattern and for the same reason.
///
/// The migration's whole purpose is a value nobody can see in a row: what the <c>Role</c> column
/// gives an <c>INSERT</c> that states no role. So the schema is read back from SQLite itself
/// before and after, and the thing that must *not* change - every existing member's exact role,
/// and therefore their access - is proved on the same database, through the real adapter and the
/// real policy.
/// </summary>
public class BusinessMembershipRoleDatabaseDefaultMigrationTests
{
    private const string PreviousMigration = "20261013123146_AddMachineStockCounts";
    private const string DefaultMigration = "20261015050929_RemoveBusinessMembershipRoleOwnerDatabaseDefault";

    private const string CreatedAtUtc = "2026-01-01 03:04:05";

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string OwnerOid = "22222222-2222-2222-2222-222222222222";
    private const string ManagerOid = "33333333-3333-3333-3333-333333333333";
    private const string OperatorOid = "44444444-4444-4444-4444-444444444444";
    private const string UndeclaredOid = "55555555-5555-5555-5555-555555555555";

    /// <summary>The stored roles, as the rows carry them and the migration must leave them.</summary>
    private const long OperatorRole = 20;
    private const long ManagerRole = 30;
    private const long OwnerRole = 40;

    /// <summary>A role no deployment of this code declares, carried by a hand-written row.</summary>
    private const long UnknownRole = 99;

    /// <summary>The default after the upgrade: a value no role is declared at.</summary>
    private const long UndeclaredRole = 0;

    /// <summary><c>BusinessStatus.Active</c>'s stored value.</summary>
    private const long ActiveBusiness = 20;

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

    private static Task SeedBusinessAsync(SqliteConnection connection) =>
        ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Trading Vending Co', {ActiveBusiness}, '{CreatedAtUtc}', 'Australia/Sydney');
            """);

    /// <summary>
    /// One membership per declared role, plus one carrying a role this code does not declare, so
    /// every stored value the column can hold is covered by the upgrade. Each identity is
    /// different, because at most one active membership per <c>(tid, oid)</c> pair is allowed.
    /// </summary>
    private static Task SeedEveryRoleAsync(SqliteConnection connection) =>
        ExecuteAsync(connection, $"""
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (1, 1, '{Tid}', '{OwnerOid}', {OwnerRole}, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (2, 1, '{Tid}', '{ManagerOid}', {ManagerRole}, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (3, 1, '{Tid}', '{OperatorOid}', {OperatorRole}, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (4, 1, '{Tid}', '{UndeclaredOid}', {UnknownRole}, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            """);

    private static Task<string?> RoleDefaultAsync(SqliteConnection connection) =>
        MigrationSchemaProbe.ColumnDefaultAsync(connection, "BusinessMemberships", "Role");

    private static async Task<BusinessMembershipResolution> ResolveAsync(AppDbContext db, string oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, oid, out var actor));
        return BusinessMembershipResolutionPolicy.Resolve(
            await new Inventory.Infrastructure.Persistence.EfBusinessMembershipStore(db)
                .FindMembershipsAsync(actor!, CancellationToken.None));
    }

    /// <summary>
    /// The one thing that changes is the default, and the schema says so both before and after:
    /// the column arrives defaulting to <c>Owner</c> and leaves defaulting to a value that is not
    /// a role, while every row keeps the role it had and the table gains no leftover column.
    /// </summary>
    [Fact]
    public async Task The_migration_replaces_the_owner_default_and_preserves_every_role()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        Assert.Equal(OwnerRole.ToString(), await RoleDefaultAsync(connection));

        await SeedBusinessAsync(connection);
        await SeedEveryRoleAsync(connection);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships");

        await MigrateToAsync(db, DefaultMigration);

        Assert.Equal(UndeclaredRole.ToString(), await RoleDefaultAsync(connection));

        Assert.Equal(OwnerRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(ManagerRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 2;"));
        Assert.Equal(OperatorRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 3;"));

        // A role nobody declares is preserved exactly as it was too: the migration moves values, it
        // does not repair or reinterpret them, and this one keeps being denied on every request.
        Assert.Equal(UnknownRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 4;"));
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));

        // The column moves to the end of the table, which nothing reads, and the column the role
        // travelled through is gone rather than left behind as a second copy of the access decision.
        var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        Assert.Equal(columnsBefore.OrderBy(name => name), columnsAfter.OrderBy(name => name));
        Assert.DoesNotContain("RoleWithoutOwnerDefault", columnsAfter);

        // No table rebuild, so the indexes this table's rules depend on - the filtered one-active-
        // membership index of issue #522 among them - are exactly the ones that were there.
        Assert.Equal(
            indexesBefore,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships"));

        // And the rest of each row came through the copy untouched.
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT BusinessId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(OwnerOid, await ScalarAsync(connection, "SELECT ObjectId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(
            CreatedAtUtc,
            await ScalarAsync(connection, "SELECT CreatedAtUtc FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Null(await ScalarAsync(connection, "SELECT Email FROM BusinessMemberships WHERE Id = 1;"));
    }

    /// <summary>
    /// After the upgrade, an <c>INSERT</c> that omits the column - the case the whole migration
    /// exists for - produces a membership that grants nothing, rather than an Owner.
    /// </summary>
    [Fact]
    public async Task After_the_upgrade_an_insert_without_a_role_is_not_an_owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, DefaultMigration);

        await SeedBusinessAsync(connection);
        await ExecuteAsync(connection, $"""
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (9, 1, '{Tid}', '{OwnerOid}', 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            """);

        var stored = (long)(await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 9;"))!;

        Assert.Equal(UndeclaredRole, stored);
        Assert.NotEqual(OwnerRole, stored);
        Assert.False(BusinessRoles.IsSupported((BusinessRole)stored));

        var resolution = await ResolveAsync(db, OwnerOid);
        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.RoleUnrecognised, resolution.DenialReason);
    }

    /// <summary>
    /// A database with no membership yet - the state a fresh deployment upgrades from - upgrades
    /// the same way and invents no row to copy.
    /// </summary>
    [Fact]
    public async Task The_migration_invents_no_membership_when_there_is_none_to_copy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, DefaultMigration);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal(UndeclaredRole.ToString(), await RoleDefaultAsync(connection));
    }

    /// <summary>
    /// The migration's actual claim: nobody's access changes across it. Each member still resolves
    /// with exactly the role they held, and the one holding an undeclared role is still denied -
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

        await SeedBusinessAsync(connection);
        await SeedEveryRoleAsync(connection);

        await MigrateToAsync(db, DefaultMigration);

        var owner = await ResolveAsync(db, OwnerOid);
        Assert.True(owner.IsResolved);
        Assert.Equal(BusinessId.From(1), owner.ResolvedBusinessId);
        Assert.Equal(BusinessRole.Owner, owner.ResolvedRole);

        var manager = await ResolveAsync(db, ManagerOid);
        Assert.True(manager.IsResolved);
        Assert.Equal(BusinessRole.Manager, manager.ResolvedRole);

        var operatorMember = await ResolveAsync(db, OperatorOid);
        Assert.True(operatorMember.IsResolved);
        Assert.Equal(BusinessRole.Operator, operatorMember.ResolvedRole);

        var undeclared = await ResolveAsync(db, UndeclaredOid);
        Assert.False(undeclared.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.RoleUnrecognised, undeclared.DenialReason);
    }

    /// <summary>
    /// <c>Down</c> returns the schema issue #521 left - including, deliberately, its <c>Owner</c>
    /// default - and preserves every role on the way back, so a rollback changes nobody's access
    /// either.
    /// </summary>
    [Fact]
    public async Task The_downgrade_restores_the_previous_default_and_preserves_every_role()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedBusinessAsync(connection);
        await SeedEveryRoleAsync(connection);
        await MigrateToAsync(db, DefaultMigration);

        await MigrateToAsync(db, PreviousMigration);

        Assert.Equal(OwnerRole.ToString(), await RoleDefaultAsync(connection));

        Assert.Equal(OwnerRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(ManagerRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 2;"));
        Assert.Equal(OperatorRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 3;"));
        Assert.Equal(UnknownRole, await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 4;"));
        Assert.DoesNotContain(
            "RoleWithoutOwnerDefault",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships"));
    }
}
