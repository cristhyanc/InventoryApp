using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessMembershipRole</c> (issue #521).
///
/// This migration touches the one table that decides who may sign in, so what it must prove is
/// narrow and absolute: it adds a single column to <c>BusinessMemberships</c>, every membership
/// that already existed comes out as <see cref="BusinessRole.Owner"/> - the access those members
/// have today - and no other column, row or table changes. Nobody loses access and nobody gains
/// any across the upgrade.
///
/// Relational SQLite rather than InMemory, because the backfilled default is the migration's own
/// behaviour and only the real schema has it.
/// </summary>
public class AddBusinessMembershipRoleMigrationTests
{
    private const string PreviousMigration = "20261010072903_AddBusinessNayaxConnections";
    private const string RoleMigration = "AddBusinessMembershipRole";
    private const string CreatedAtUtc = "2026-01-01 03:04:05";
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string FirstOid = "22222222-2222-2222-2222-222222222222";
    private const string SecondOid = "33333333-3333-3333-3333-333333333333";

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
    public async Task The_migration_adds_the_column_and_backfills_every_existing_membership_to_Owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(
            "Role",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships"));

        // Seeded with raw SQL, not the EF model: the model already knows about a column this
        // older schema has not created yet. Two memberships, one of them revoked, so the backfill
        // is seen to cover every row rather than only the reachable ones.
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO BusinessMemberships (Id, BusinessId, DirectoryTenantId, ObjectId, IsActive, CreatedAtUtc)
                VALUES (1, 1, '{Tid}', '{FirstOid}', 1, '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships (Id, BusinessId, DirectoryTenantId, ObjectId, IsActive, CreatedAtUtc)
                VALUES (2, 1, '{Tid}', '{SecondOid}', 0, '{CreatedAtUtc}');
            """);

        await MigrateToAsync(db, RoleMigration);

        Assert.Contains(
            "Role",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships"));

        // Every existing membership keeps exactly the access it has today, stored as the declared
        // Owner value rather than as a name or as whatever an unfilled column would hold.
        var owner = ((int)BusinessRole.Owner).ToString();
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal(
            "2",
            await ScalarAsync(connection, $"SELECT COUNT(*) FROM BusinessMemberships WHERE Role = {owner};"));

        // And nothing else about either row moved - identity, revocation state and creation
        // instant included.
        Assert.Equal(FirstOid, await ScalarAsync(connection, "SELECT ObjectId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(Tid, await ScalarAsync(connection, "SELECT DirectoryTenantId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 2;"));
        Assert.Equal(CreatedAtUtc, await ScalarAsync(connection, "SELECT CreatedAtUtc FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
        Assert.Equal("Vending Co", await ScalarAsync(connection, "SELECT Name FROM Businesses WHERE Id = 1;"));
    }

    /// <summary>
    /// The backfilled membership resolves exactly as it did before the upgrade: through the real
    /// adapter and the real policy, to its business, as an Owner. This is the test that says "no
    /// existing user is locked out", because a backfilled value the resolution policy did not
    /// recognise would deny every one of them.
    /// </summary>
    [Fact]
    public async Task A_backfilled_membership_still_resolves_and_does_so_as_an_Owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO BusinessMemberships (Id, BusinessId, DirectoryTenantId, ObjectId, IsActive, CreatedAtUtc)
                VALUES (1, 1, '{Tid}', '{FirstOid}', 1, '{CreatedAtUtc}');
            """);

        await MigrateToAsync(db, RoleMigration);

        Assert.True(ActorIdentity.TryCreate(Tid, FirstOid, out var actor));
        var memberships = await new Inventory.Infrastructure.Persistence.EfBusinessMembershipStore(db)
            .FindMembershipsAsync(actor!, CancellationToken.None);

        var resolution = BusinessMembershipResolutionPolicy.Resolve(memberships);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(1), resolution.ResolvedBusinessId);
        Assert.Equal(BusinessRole.Owner, resolution.ResolvedRole);
    }

    /// <summary>
    /// A database with no membership yet - the state a fresh deployment migrates from - upgrades
    /// the same way and invents no membership to backfill. An approval is only ever created by an
    /// explicit, human-supplied bootstrap.
    /// </summary>
    [Fact]
    public async Task The_migration_invents_no_membership_when_there_is_none_to_backfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, RoleMigration);

        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
    }
}
