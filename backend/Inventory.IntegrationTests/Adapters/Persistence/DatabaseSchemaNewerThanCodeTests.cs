using InventoryApi.Bootstrap;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// A database whose schema is <i>newer</i> than the code (issue #1299, audit #1228 finding OPS-03,
/// confirmed by #1231).
///
/// <see cref="DatabaseSchemaStartupTests"/> covers the pending-migration half of the startup
/// decision and <see cref="DatabaseMigratorTests"/> the explicit command's; both only ever ask what
/// is pending, which is why an older build started against a database a newer build had already
/// migrated used to find nothing pending, start normally, answer <c>/health/ready</c> with 200 and
/// write to a schema it does not understand - and why the dry run called it "already up to date".
/// Every test here seeds the condition the way reality produces it: a real
/// <c>__EFMigrationsHistory</c> row carrying a migration id this build does not contain, against a
/// relational SQLite database.
/// </summary>
public class DatabaseSchemaNewerThanCodeTests : IDisposable
{
    /// <summary>
    /// Sorts after every real migration id, which are <c>yyyyMMddHHmmss_Name</c>, so the fixture
    /// also stands for the realistic case: the next release's migration, applied by the build that
    /// shipped it.
    /// </summary>
    private const string MigrationFromANewerBuild = "29990101000000_AddSomethingThisBuildNeverHeardOf";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly StringWriter _output = new();

    public DatabaseSchemaNewerThanCodeTests()
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "InventoryApi";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Captures the level as well as the message: the environment rule is about both.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public string MessagesAt(LogLevel level) =>
            string.Join('\n', Entries.Where(entry => entry.Level == level).Select(entry => entry.Message));

        private sealed class CapturingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private void EnsureSchema(string environmentName, ILoggerFactory? loggerFactory = null)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        DatabaseSchemaStartup.EnsureSchema(
            db,
            new TestHostEnvironment { EnvironmentName = environmentName },
            new ConfigurationBuilder().Build(),
            loggerFactory ?? NullLoggerFactory.Instance);
    }

    private void MigrateFully()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        db.GetService<IMigrator>().Migrate();
    }

    /// <summary>
    /// Migrates to every migration except the last, so the database is exactly one behind, and
    /// returns the id left pending.
    /// </summary>
    private string MigrateToOneBehind()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        var all = db.Database.GetMigrations().ToList();
        db.GetService<IMigrator>().Migrate(all[^2]);
        return all[^1];
    }

    /// <summary>
    /// Records an applied migration this build does not contain, exactly as the newer build that
    /// applied it would have: one row in the history table EF Core itself reads.
    /// </summary>
    private void RecordAppliedMigrationFromANewerBuild(string migrationId = MigrationFromANewerBuild)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, '99.0.0');";
        command.Parameters.AddWithValue("$id", migrationId);
        command.ExecuteNonQuery();
    }

    private List<string> AppliedMigrationIds()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        using var reader = command.ExecuteReader();

        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>Every table, index, trigger and view with its definition, as the database holds it.</summary>
    private List<string> SchemaObjects()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT type || '|' || name || '|' || COALESCE(sql, '') FROM sqlite_master "
            + "ORDER BY type, name;";
        using var reader = command.ExecuteReader();

        var objects = new List<string>();
        while (reader.Read())
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }

    private List<string> ReportedLines() =>
        _output.ToString()
            .Split(Environment.NewLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    /// <summary>
    /// The refusal this change exists for: Production will not start against a schema a newer build
    /// has already migrated, it names the unknown id so an operator knows which release they are
    /// behind, and it logs that critically rather than only throwing.
    /// </summary>
    [Fact]
    public void Production_startup_refuses_a_schema_newer_than_the_code_and_names_the_unknown_applied_ids()
    {
        MigrateFully();
        RecordAppliedMigrationFromANewerBuild();
        var loggerFactory = new CapturingLoggerFactory();

        var exception = Assert.Throws<DatabaseSchemaNewerThanCodeException>(
            () => EnsureSchema("Production", loggerFactory));

        Assert.Contains(MigrationFromANewerBuild, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Production", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            MigrationFromANewerBuild,
            loggerFactory.MessagesAt(LogLevel.Critical),
            StringComparison.Ordinal);

        // The operator is told how to get back, because a migration is never rolled back.
        Assert.Contains("restoring the verified backup", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordering requirement, and the reason the check is the first thing
    /// <c>EnsureSchema</c> does: a database holding an unknown applied id <i>and</i> a pending one
    /// is refused with the pending migration not applied. Both the history table and the schema
    /// itself are compared before and after, so "nothing was written" is asserted rather than
    /// assumed from the exception type.
    /// </summary>
    [Fact]
    public void A_pending_migration_is_not_applied_when_an_unknown_applied_id_is_present()
    {
        var pendingMigration = MigrateToOneBehind();
        RecordAppliedMigrationFromANewerBuild();

        var historyBefore = AppliedMigrationIds();
        var schemaBefore = SchemaObjects();

        Assert.Throws<DatabaseSchemaNewerThanCodeException>(() => EnsureSchema("Production"));

        Assert.Equal(historyBefore, AppliedMigrationIds());
        Assert.Equal(schemaBefore, SchemaObjects());
        Assert.DoesNotContain(pendingMigration, AppliedMigrationIds());

        // And it is still genuinely pending afterwards: the refusal did not half-apply it.
        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Contains(pendingMigration, db.Database.GetPendingMigrations());
    }

    /// <summary>
    /// A Staging-like environment holds somebody's hosted data, so it fails closed with Production
    /// rather than with the developer's laptop - and it does so even with no migration pending at
    /// all, which is the case the pending-only check could never see.
    /// </summary>
    [Fact]
    public void Another_non_production_environment_refuses_a_schema_newer_than_the_code_too()
    {
        MigrateFully();
        RecordAppliedMigrationFromANewerBuild();

        var exception = Assert.Throws<DatabaseSchemaNewerThanCodeException>(() => EnsureSchema("Staging"));

        Assert.Contains("Staging", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Development and <c>Testing</c> follow the existing environment rule in
    /// <c>DatabaseSchemaStartup</c>: their database is the disposable one this policy already lets
    /// startup migrate automatically, so the same condition is an error in the log and startup
    /// continues, applying whatever is pending as it always did.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_and_testing_log_an_error_and_continue(string environmentName)
    {
        var pendingMigration = MigrateToOneBehind();
        RecordAppliedMigrationFromANewerBuild();
        var loggerFactory = new CapturingLoggerFactory();

        EnsureSchema(environmentName, loggerFactory);

        var errors = loggerFactory.MessagesAt(LogLevel.Error);
        Assert.Contains(MigrationFromANewerBuild, errors, StringComparison.Ordinal);
        Assert.Contains(environmentName, errors, StringComparison.Ordinal);
        Assert.Empty(loggerFactory.Entries.Where(entry => entry.Level == LogLevel.Critical));

        // The existing behaviour of these two environments is unchanged: startup carried on and
        // applied what was pending.
        Assert.Contains(pendingMigration, AppliedMigrationIds());
    }

    /// <summary>
    /// The regression guard for every normal deployment: a database whose applied ids are all known
    /// is untouched by this check, in the environment that refuses and in the ones that do not.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void A_database_whose_applied_ids_are_all_known_starts_exactly_as_before(string environmentName)
    {
        MigrateFully();
        var historyBefore = AppliedMigrationIds();
        var loggerFactory = new CapturingLoggerFactory();

        EnsureSchema(environmentName, loggerFactory);

        Assert.Empty(loggerFactory.Entries.Where(entry =>
            entry.Level is LogLevel.Error or LogLevel.Critical));
        Assert.Equal(historyBefore, AppliedMigrationIds());

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.Empty(UnknownAppliedMigrations.Detect(db.Database));
    }

    /// <summary>
    /// The dry run's half of the issue: it lists the unknown applied ids and exits non-zero, where
    /// it used to report "already up to date" and succeed - which is what let a restore or a
    /// rollback onto an older build look verified.
    /// </summary>
    [Fact]
    public async Task Dry_run_lists_the_unknown_applied_ids_and_exits_non_zero_instead_of_up_to_date()
    {
        MigrateFully();
        RecordAppliedMigrationFromANewerBuild();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, _output, CancellationToken.None);

        Assert.Equal(new[] { MigrationFromANewerBuild }, outcome.UnknownApplied);
        Assert.True(outcome.SchemaIsNewerThanThisBuild);
        Assert.Equal(1, outcome.ExitCode);
        Assert.False(outcome.Applied);

        var lines = ReportedLines();
        Assert.Contains($"  unknown  {MigrationFromANewerBuild}", lines);
        Assert.DoesNotContain(lines, line => line.Contains("already up to date", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("NEWER than this build", StringComparison.Ordinal));
    }

    /// <summary>
    /// The unknown-plus-pending case through the command: both are reported, the exit code is
    /// non-zero, and a dry run still applies nothing.
    /// </summary>
    [Fact]
    public async Task Dry_run_reports_an_unknown_applied_id_beside_a_pending_one_and_applies_nothing()
    {
        var pendingMigration = MigrateToOneBehind();
        RecordAppliedMigrationFromANewerBuild();
        var historyBefore = AppliedMigrationIds();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, _output, CancellationToken.None);

        Assert.Equal(new[] { MigrationFromANewerBuild }, outcome.UnknownApplied);
        Assert.Equal(new[] { pendingMigration }, outcome.PendingBefore);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(historyBefore, AppliedMigrationIds());

        var lines = ReportedLines();
        Assert.Contains($"  unknown  {MigrationFromANewerBuild}", lines);
        Assert.Contains($"  pending  {pendingMigration}", lines);
    }

    /// <summary>
    /// The dry run of a database this build does understand is unchanged: no unknown ids, no new
    /// report lines, exit code 0 and the same "already up to date" sentence operators and the
    /// restore rehearsal already look for.
    /// </summary>
    [Fact]
    public async Task Dry_run_of_a_known_schema_still_reports_up_to_date_and_exits_zero()
    {
        MigrateFully();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, _output, CancellationToken.None);

        Assert.Empty(outcome.UnknownApplied);
        Assert.False(outcome.SchemaIsNewerThanThisBuild);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("The schema is already up to date. Nothing to do.", ReportedLines());
        Assert.DoesNotContain(ReportedLines(), line => line.Contains("unknown", StringComparison.Ordinal));
    }

    /// <summary>
    /// Several unknown ids are all named, and in a stable order, so the operator sees how far ahead
    /// the database is rather than only that it is ahead.
    /// </summary>
    [Fact]
    public async Task Every_unknown_applied_id_is_reported_in_a_stable_order()
    {
        MigrateFully();
        RecordAppliedMigrationFromANewerBuild("29990301000000_ThirdFutureMigration");
        RecordAppliedMigrationFromANewerBuild("29990201000000_SecondFutureMigration");

        await using var db = TestAppDbContext.Unrestricted(_options);
        var outcome = await DatabaseMigrator.RunAsync(db, apply: false, _output, CancellationToken.None);

        Assert.Equal(
            new[] { "29990201000000_SecondFutureMigration", "29990301000000_ThirdFutureMigration" },
            outcome.UnknownApplied);
        Assert.Equal(1, outcome.ExitCode);
    }

    /// <summary>
    /// The comparison itself, over the two lists it is given: an applied id the build does not
    /// contain is unknown, a known one is not, and a migration the build contains but the database
    /// has not applied is pending rather than unknown - the direction this check must not confuse.
    /// </summary>
    [Fact]
    public void Only_applied_ids_the_build_does_not_contain_are_unknown()
    {
        var known = new[] { "1_First", "2_Second", "3_Third" };

        Assert.Empty(UnknownAppliedMigrations.In(new[] { "1_First", "2_Second" }, known));
        Assert.Equal(
            new[] { "4_Fourth" },
            UnknownAppliedMigrations.In(new[] { "1_First", "4_Fourth" }, known));
        Assert.Equal(
            new[] { "4_Fourth" },
            UnknownAppliedMigrations.In(new[] { "4_Fourth", "4_Fourth" }, known));
        Assert.Empty(UnknownAppliedMigrations.In(Array.Empty<string>(), known));
    }
}
