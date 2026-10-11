using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddMemberDisplayNameAndEmail</c> (issue #524).
///
/// The migration touches the table that decides who may sign in, so "additive" has to be proved
/// rather than asserted: the two columns arrive, every existing row keeps every value it had,
/// nobody's membership, role or revocation state moves, the indexes - including the filtered
/// one-active-membership index of issue #522 - are untouched, and nothing is backfilled into the
/// new columns.
///
/// Relational SQLite rather than InMemory, because a column addition on an existing table, a
/// preserved filtered index and "no table was rebuilt" are relational facts.
/// </summary>
public class AddMemberDisplayNameAndEmailMigrationTests
{
    private const string PreviousMigration = "20261010223128_AddOneActiveMembershipPerIdentity";
    private const string DisplayDetailsMigration = "AddMemberDisplayNameAndEmail";

    private const string ActiveIndex = "IX_BusinessMemberships_DirectoryTenantId_ObjectId_Active";
    private const string LookupIndex = "IX_BusinessMemberships_DirectoryTenantId_ObjectId";
    private const string PerBusinessIndex = "IX_BusinessMemberships_DirectoryTenantId_ObjectId_BusinessId";

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string FirstOid = "22222222-2222-2222-2222-222222222222";
    private const string SecondOid = "33333333-3333-3333-3333-333333333333";
    private const string CreatedAtUtc = "2026-01-01 03:04:05";
    private const string StatusChangedAtUtc = "2026-06-02 07:08:09";

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
    /// Seeded with raw SQL rather than the EF model, because the model already knows about two
    /// columns the older schema has not created yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, IsActive, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 1, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (1, 1, '{Tid}', '{FirstOid}', 40, 1, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (2, 1, '{Tid}', '{SecondOid}', 30, 0, '{CreatedAtUtc}', '{StatusChangedAtUtc}');
            """);

    [Fact]
    public async Task The_migration_adds_two_nullable_columns_and_changes_no_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        var before = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        Assert.DoesNotContain("DisplayName", before);
        Assert.DoesNotContain("Email", before);

        await SeedPreviousSchemaAsync(connection);

        await MigrateToAsync(db, DisplayDetailsMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        Assert.Contains("DisplayName", columns);
        Assert.Contains("Email", columns);

        // Nullable, and nothing was backfilled into either column: there is nothing in the database
        // a person's name could honestly be inferred from, and the only thing that ever writes them
        // is that member's own validated token.
        Assert.Equal(
            "2",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM BusinessMemberships WHERE DisplayName IS NULL AND Email IS NULL;"));

        // Every pre-existing row is exactly as it was - identity, business, role, revocation state
        // and both instants.
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        Assert.Equal(FirstOid, await ScalarAsync(connection, "SELECT ObjectId FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("40", await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal("30", await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships WHERE Id = 2;"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT IsActive FROM BusinessMemberships WHERE Id = 2;"));
        Assert.Equal(
            CreatedAtUtc,
            await ScalarAsync(connection, "SELECT CreatedAtUtc FROM BusinessMemberships WHERE Id = 1;"));
        Assert.Equal(
            StatusChangedAtUtc,
            await ScalarAsync(connection, "SELECT StatusChangedAtUtc FROM BusinessMemberships WHERE Id = 2;"));

        // And every index the sign-in path and issue #522's rule depend on is still there, filter
        // included: a column addition must not have rebuilt this table.
        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessMemberships");
        Assert.Contains(ActiveIndex, indexes);
        Assert.Contains(LookupIndex, indexes);
        Assert.Contains(PerBusinessIndex, indexes);

        var indexSql = await ScalarAsync(
            connection,
            $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{ActiveIndex}';");
        Assert.Contains("UNIQUE", indexSql!, StringComparison.Ordinal);
        Assert.Contains("WHERE \"IsActive\" = 1", indexSql!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fresh deployment, with no membership at all, upgrades the same way.
    /// </summary>
    [Fact]
    public async Task An_empty_database_upgrades_the_same_way()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, DisplayDetailsMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        Assert.Contains("DisplayName", columns);
        Assert.Contains("Email", columns);
        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
    }

    /// <summary>
    /// The whole migration history still applies in order, and the schema it produces is the one
    /// the model describes - the check an unattended Production startup depends on.
    /// </summary>
    [Fact]
    public async Task The_full_migration_history_applies_and_leaves_no_pending_model_change()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships");
        Assert.Contains("DisplayName", columns);
        Assert.Contains("Email", columns);
    }
}
