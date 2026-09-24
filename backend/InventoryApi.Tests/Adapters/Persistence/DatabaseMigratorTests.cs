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
/// what happens at API startup; this covers what the explicit human-invoked command itself does,
/// including what it reports to the operator and in what order.
/// </summary>
public class DatabaseMigratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly StringWriter _output = new();

    public DatabaseMigratorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose()
    {
        _output.Dispose();
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

    /// <summary>The report as written so far, with blank padding lines removed.</summary>
    private List<string> ReportedLines() =>
        _output.ToString()
            .Split(Environment.NewLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private int IndexOfLineContaining(string fragment)
    {
        var lines = ReportedLines();
        var index = lines.FindIndex(line => line.Contains(fragment, StringComparison.Ordinal));
        Assert.True(index >= 0, $"Expected a reported line containing '{fragment}'. Report was:{Environment.NewLine}{_output}");
        return index;
    }

    [Fact]
    public async Task Dry_run_one_migration_behind_reports_the_pending_migration_without_applying_it()
    {
        var lastMigration = MigrateToOneBehind();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, _output, CancellationToken.None);

        Assert.False(outcome.WasUpToDate);
        Assert.False(outcome.Applied);
        Assert.Equal(new[] { lastMigration }, outcome.PendingBefore);
        Assert.Equal(outcome.PendingBefore, outcome.PendingAfter);
        Assert.Null(outcome.Readiness);
        Assert.Equal(0, outcome.ExitCode);
        Assert.DoesNotContain(lastMigration, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Dry_run_up_to_date_reports_no_pending_migrations()
    {
        MigrateFully();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, _output, CancellationToken.None);

        Assert.True(outcome.WasUpToDate);
        Assert.False(outcome.Applied);
        Assert.Empty(outcome.PendingAfter);
        Assert.Null(outcome.Readiness);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task Apply_one_migration_behind_applies_it_and_reports_readiness()
    {
        var lastMigration = MigrateToOneBehind();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: true, _output, CancellationToken.None);

        Assert.False(outcome.WasUpToDate);
        Assert.True(outcome.Applied);
        Assert.True(outcome.FullyApplied);
        Assert.Empty(outcome.PendingAfter);
        Assert.NotNull(outcome.Readiness);
        Assert.Equal(0, outcome.ExitCode);
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
        var outcome = await DatabaseMigrator.RunAsync(db, apply: true, _output, CancellationToken.None);

        Assert.True(outcome.WasUpToDate);
        Assert.False(outcome.Applied);
        Assert.Null(outcome.Readiness);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("The schema is already up to date. Nothing to do.", ReportedLines());
    }

    /// <summary>
    /// The operator decides whether to let a production apply run from what the command printed
    /// first, so the summary, the pending migration names and "Applying..." must all appear ahead
    /// of the outcome rather than being reported together once the migration has finished.
    /// </summary>
    [Fact]
    public async Task Apply_reports_the_summary_pending_names_and_Applying_before_the_result()
    {
        var lastMigration = MigrateToOneBehind();

        await using var db = TestAppDbContext.Unrestricted(_options);
        await DatabaseMigrator.RunAsync(db, apply: true, _output, CancellationToken.None);

        var header = IndexOfLineContaining("Database migration - APPLY");
        var appliedCount = IndexOfLineContaining("Applied migrations : ");
        var pendingCount = IndexOfLineContaining("Pending migrations : 1");
        var pendingName = IndexOfLineContaining($"  pending  {lastMigration}");
        var applying = IndexOfLineContaining("Applying...");
        var result = IndexOfLineContaining("Schema applied. No migrations remain pending.");
        var readiness = IndexOfLineContaining("Tenant ownership is ");

        Assert.True(
            header < appliedCount && appliedCount < pendingCount && pendingCount < pendingName
                && pendingName < applying && applying < result && result < readiness,
            $"Report is out of order:{Environment.NewLine}{_output}");
    }

    /// <summary>
    /// A production apply can fail part-way. When it does, the operator must still be looking at
    /// the summary, the names of the migrations that were about to run, and "Applying...", because
    /// that is what tells them how far the attempt got and what the database may now contain.
    /// </summary>
    [Fact]
    public async Task Apply_that_fails_still_leaves_the_operator_the_summary_pending_names_and_Applying()
    {
        var lastMigration = MigrateToOneBehind();
        MakeDatabaseRejectWrites();

        await using var db = TestAppDbContext.Unrestricted(_options);

        await Assert.ThrowsAnyAsync<SqliteException>(
            () => DatabaseMigrator.RunAsync(db, apply: true, _output, CancellationToken.None));

        var lines = ReportedLines();
        Assert.Contains("Database migration - APPLY", lines);
        Assert.Contains("Pending migrations : 1", lines);
        Assert.Contains($"  pending  {lastMigration}", lines);
        Assert.Equal("Applying...", lines[^1]);
        Assert.DoesNotContain(lines, line => line.Contains("Schema applied", StringComparison.Ordinal));
    }

    /// <summary>
    /// Makes every subsequent write on this connection fail, which is how a migration that cannot
    /// be applied behaves against a real database.
    /// </summary>
    private void MakeDatabaseRejectWrites()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = 1;";
        command.ExecuteNonQuery();
    }
}
