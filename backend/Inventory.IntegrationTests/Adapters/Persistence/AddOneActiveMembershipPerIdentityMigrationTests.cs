using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddOneActiveMembershipPerIdentity</c> (issue #522).
///
/// The migration touches the table that decides who may sign in, and it is the first migration in
/// this repository that may deliberately refuse to apply. Both halves therefore have to be proved
/// on a real schema: that a database whose data supports the rule upgrades additively and keeps
/// every row exactly as it was, and that a database whose data does not is left *completely*
/// untouched - no column, no index, no row, and no history entry claiming the migration ran.
///
/// Relational SQLite rather than InMemory: a filtered unique index, a <c>RAISE(ABORT, ...)</c> and
/// a rolled-back migration are relational behaviour that InMemory does not have.
/// </summary>
public class AddOneActiveMembershipPerIdentityMigrationTests
{
    private const string PreviousMigration = "20261010124826_AddBusinessMembershipRole";
    private const string OneActiveMembershipMigration = "AddOneActiveMembershipPerIdentity";
    private const string ActiveIndex = "IX_BusinessMemberships_DirectoryTenantId_ObjectId_Active";
    private const string LookupIndex = "IX_BusinessMemberships_DirectoryTenantId_ObjectId";
    private const string FirstCreatedAtUtc = "2026-01-01 03:04:05";
    private const string SecondCreatedAtUtc = "2026-02-02 06:07:08";
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

    /// <summary>
    /// The membership rows an upgrade starts from, seeded with raw SQL rather than the EF model:
    /// the model already knows about a column the older schema has not created yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection, string memberships) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{FirstCreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (2, 'Other Vending Co', 1, '{FirstCreatedAtUtc}', 'Australia/Sydney');
            {memberships}
            """);

    private static string Membership(
        int id,
        int businessId,
        string oid,
        bool isActive,
        string createdAtUtc = FirstCreatedAtUtc,
        string tid = Tid) =>
        $"""
        INSERT INTO BusinessMemberships (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc)
            VALUES ({id}, {businessId}, '{tid}', '{oid}', 40, {(isActive ? 1 : 0)}, '{createdAtUtc}');
        """;

    [Fact]
    public async Task The_migration_adds_the_column_and_the_index_and_changes_no_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(
            "StatusChangedAtUtc",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships"));
        Assert.DoesNotContain(
            ActiveIndex,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships"));

        // One active membership and one revoked one, in two different businesses, for two
        // different people - the shape the rule allows and the shape a live database has.
        await SeedPreviousSchemaAsync(
            connection,
            Membership(1, 1, FirstOid, isActive: true)
                + Membership(2, 2, SecondOid, isActive: false, createdAtUtc: SecondCreatedAtUtc));

        await MigrateToAsync(db, OneActiveMembershipMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        Assert.Contains("StatusChangedAtUtc", columns);

        // Every pre-existing row's state instant is its creation instant: the only one the row
        // records. The revoked row included - its revocation instant was never stored, so claiming
        // one would invent history.
        Assert.Equal(
            "0",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM BusinessMemberships WHERE StatusChangedAtUtc <> CreatedAtUtc;"));
        Assert.Equal(
            FirstCreatedAtUtc,
            await ScalarAsync(connection, "SELECT StatusChangedAtUtc FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(
            SecondCreatedAtUtc,
            await ScalarAsync(connection, "SELECT StatusChangedAtUtc FROM BusinessMemberships WHERE Id = 2;"));

        // Nothing else about either row moved: identity, business, role and revocation state alike.
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal(FirstOid, await ScalarAsync(connection, "SELECT ObjectId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT BusinessId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("40", await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 2;"));

        // The new index is unique and filtered on the active rows, and the lookup index the
        // per-request membership resolution uses is still there: a filtered index cannot serve a
        // lookup that must also see revoked rows.
        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships");
        Assert.Contains(ActiveIndex, indexes);
        Assert.Contains(LookupIndex, indexes);
        Assert.Contains("IX_BusinessMemberships_DirectoryTenantId_ObjectId_BusinessId", indexes);

        var indexSql = await ScalarAsync(
            connection,
            $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{ActiveIndex}';");
        Assert.Contains("UNIQUE", indexSql!, StringComparison.Ordinal);
        Assert.Contains("WHERE \"IsActive\" = 1", indexSql!, StringComparison.Ordinal);

        // And the guard's temporary table and trigger left nothing behind.
        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain("OneActiveMembershipGuard", tables);
    }

    /// <summary>
    /// A fresh deployment, with no membership to check or fill, upgrades the same way and invents
    /// nothing.
    /// </summary>
    [Fact]
    public async Task The_migration_applies_to_an_empty_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, OneActiveMembershipMigration);

        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Contains(
            ActiveIndex,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships"));
    }

    /// <summary>
    /// The refusal, and what it must leave behind: nothing. The migration states what to fix,
    /// changes no column, no index and no row, and does not record itself as applied - so the
    /// upgrade can simply be run again once a human has revoked the extra membership.
    ///
    /// Two identical GUID pairs differing only in casing are the same identity (both columns are
    /// NOCASE), so the check has to refuse that pair too: a case-shifted row must not be a way to
    /// get past the rule the index is about to enforce.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_migration_aborts_and_changes_nothing_when_an_identity_has_two_active_memberships(
        bool differentCasing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(
            connection,
            Membership(1, 1, FirstOid, isActive: true)
                + Membership(
                    2,
                    2,
                    differentCasing ? FirstOid.ToLowerInvariant() : FirstOid,
                    isActive: true,
                    createdAtUtc: SecondCreatedAtUtc,
                    tid: differentCasing ? Tid.ToUpperInvariant() : Tid));

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => MigrateToAsync(db, OneActiveMembershipMigration));

        // The message has to tell the operator what is wrong and what to do about it, and it must
        // not quote the identities: a migration log is not a place for Entra identifiers.
        Assert.Contains(
            "holds more than one active BusinessMembership",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Revoke all but one active membership", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing has been changed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstOid, exception.Message, StringComparison.OrdinalIgnoreCase);

        // "Changing nothing" in full.
        Assert.DoesNotContain(
            "StatusChangedAtUtc",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships"));
        Assert.DoesNotContain(
            ActiveIndex,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships"));
        Assert.DoesNotContain("OneActiveMembershipGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships WHERE IsActive = 1;"));
        Assert.Equal(
            "0",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%"
                    + OneActiveMembershipMigration + "';"));

        // And the refusal is repeatable rather than a one-off: the second attempt fails the same
        // way, which is what makes "fix the data, then run it again" the operator's procedure.
        await Assert.ThrowsAsync<SqliteException>(() => MigrateToAsync(db, OneActiveMembershipMigration));
    }

    /// <summary>
    /// Once the extra membership is revoked the same database upgrades, and the revoked rows stay.
    /// Any number of revoked memberships for one identity is a history, not a conflict.
    /// </summary>
    [Fact]
    public async Task Revoked_duplicates_for_one_identity_do_not_block_the_migration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(
            connection,
            Membership(1, 1, FirstOid, isActive: false)
                + Membership(2, 2, FirstOid, isActive: true, createdAtUtc: SecondCreatedAtUtc));

        await MigrateToAsync(db, OneActiveMembershipMigration);

        Assert.Contains(
            ActiveIndex,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships"));
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal(
            SecondCreatedAtUtc,
            await ScalarAsync(connection, "SELECT StatusChangedAtUtc FROM BusinessMemberships WHERE Id = 2;"));
    }

    /// <summary>
    /// The index the upgrade installs is the same rule the model declares, so a database that was
    /// migrated and one created from the model behave identically: after the upgrade, a second
    /// active membership for one identity is refused by the database itself.
    /// </summary>
    [Fact]
    public async Task After_the_upgrade_a_second_active_membership_is_refused_by_the_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection, Membership(1, 1, FirstOid, isActive: true));
        await MigrateToAsync(db, OneActiveMembershipMigration);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            Membership(2, 2, FirstOid, isActive: true, createdAtUtc: SecondCreatedAtUtc)));

        Assert.Contains("UNIQUE constraint failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
    }
}
