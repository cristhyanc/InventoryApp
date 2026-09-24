using InventoryApi.Bootstrap;
using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Exercises the detection/apply behaviour behind the <c>migrate-database</c> command (issue #54)
/// against a real relational SQLite database, one migration behind and fully up to date, as
/// called for by the issue's expected validation. <see cref="DatabaseSchemaStartupTests"/> covers
/// what happens at API startup; this covers what the explicit human-invoked command itself does.
/// </summary>
public class DatabaseMigratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public DatabaseMigratorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Migrates to every migration except the last one, so the database is exactly one migration
    /// behind. Returns the name of the migration left pending.
    /// </summary>
    private string MigrateToOneBehind()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        var all = db.Database.GetMigrations().ToList();
        var target = all[^2];
        db.GetService<IMigrator>().Migrate(target);
        return all[^1];
    }

    private void MigrateFully()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        db.GetService<IMigrator>().Migrate();
    }

    [Fact]
    public async Task Dry_run_one_migration_behind_reports_the_pending_migration_without_applying_it()
    {
        var lastMigration = MigrateToOneBehind();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, CancellationToken.None);

        Assert.False(outcome.WasUpToDate);
        Assert.False(outcome.Applied);
        Assert.Equal(new[] { lastMigration }, outcome.PendingBefore);
        Assert.Equal(outcome.PendingBefore, outcome.PendingAfter);
        Assert.Null(outcome.Readiness);
        Assert.DoesNotContain(lastMigration, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Dry_run_up_to_date_reports_no_pending_migrations()
    {
        MigrateFully();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, CancellationToken.None);

        Assert.True(outcome.WasUpToDate);
        Assert.False(outcome.Applied);
        Assert.Empty(outcome.PendingAfter);
        Assert.Null(outcome.Readiness);
    }

    [Fact]
    public async Task Apply_one_migration_behind_applies_it_and_reports_readiness()
    {
        var lastMigration = MigrateToOneBehind();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: true, CancellationToken.None);

        Assert.False(outcome.WasUpToDate);
        Assert.True(outcome.Applied);
        Assert.True(outcome.FullyApplied);
        Assert.Empty(outcome.PendingAfter);
        Assert.NotNull(outcome.Readiness);
        Assert.Contains(lastMigration, await db.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>
    /// Applying against an already up-to-date database must not be treated as a successful apply:
    /// there was nothing to migrate, so readiness is not computed and the "applied" branch never
    /// runs.
    /// </summary>
    [Fact]
    public async Task Apply_up_to_date_is_a_no_op_and_does_not_compute_readiness()
    {
        MigrateFully();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: true, CancellationToken.None);

        Assert.True(outcome.WasUpToDate);
        Assert.False(outcome.Applied);
        Assert.Null(outcome.Readiness);
    }
}
