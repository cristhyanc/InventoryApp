using InventoryApi.Bootstrap;
using InventoryApi.Data;
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
/// Startup schema behaviour (issue #64, revised by issue #201).
///
/// Issue #64 required schema changes to be applied and verified under human control, because the
/// tenancy migrations are high risk - the uniqueness one rebuilds the whole NayaxSales table - so
/// a deployment that silently applied whatever the build happened to contain would defeat the
/// review the rollout depended on. Issue #201 restores automatic migration for normal Production
/// startup once that rollout is long complete, while keeping every fail-closed guarantee: a
/// migration failure still stops the application rather than letting it serve requests against a
/// schema its code does not match, and a database with no changes reviewed and pending untested is
/// never assumed safe.
/// </summary>
public class DatabaseSchemaStartupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public DatabaseSchemaStartupTests()
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

    private static IHostEnvironment Environment(string name) =>
        new TestHostEnvironment { EnvironmentName = name };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "InventoryApi";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>Captures formatted log messages so a test can assert on what an operator would see.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }

    private void EnsureSchema(string environmentName, IConfiguration? configuration = null, ILoggerFactory? loggerFactory = null)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        DatabaseSchemaStartup.EnsureSchema(
            db,
            Environment(environmentName),
            configuration ?? Configuration(),
            loggerFactory ?? NullLoggerFactory.Instance);
    }

    private bool TenancyTablesExist() => TenancyTablesExist(_connection);

    private static bool TenancyTablesExist(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Businesses';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// The behaviour this change exists to restore: starting the API against a Production
    /// database with pending migrations applies them, rather than refusing to start.
    /// </summary>
    [Fact]
    public void Production_startup_applies_pending_migrations_automatically()
    {
        EnsureSchema("Production");

        Assert.True(TenancyTablesExist());

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    /// <summary>
    /// An already-migrated Production database starts normally and performs no schema work - the
    /// rule is "keep the schema current", not "always run every migration command".
    /// </summary>
    [Fact]
    public void Startup_outside_development_succeeds_when_no_migration_is_pending()
    {
        using (var migrated = TestAppDbContext.Unrestricted(_options))
        {
            migrated.GetService<IMigrator>().Migrate();
        }

        EnsureSchema("Production");

        Assert.True(TenancyTablesExist());
    }

    /// <summary>
    /// Repeated startup after migrations are applied is idempotent: a second automatic-migration
    /// pass over an up-to-date database does not throw and leaves nothing pending.
    /// </summary>
    [Fact]
    public void Repeated_production_startup_after_migrating_is_idempotent()
    {
        EnsureSchema("Production");
        EnsureSchema("Production");

        Assert.True(TenancyTablesExist());

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    /// <summary>
    /// Production migrates automatically regardless of the unsafe override flag: that setting is
    /// for other, disposable non-Production environments and Production's behaviour must not
    /// depend on whether it happens to be set.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public void Production_migrates_automatically_regardless_of_the_unsafe_override_flag(string? overrideValue)
    {
        var configuration = overrideValue is null
            ? Configuration()
            : Configuration((DatabaseSchemaStartup.AllowAutomaticMigrationKey, overrideValue));

        EnsureSchema("Production", configuration);

        Assert.True(TenancyTablesExist());
    }

    /// <summary>
    /// The pending migrations are named in the log before they are applied, so an operator (or a
    /// reviewer reading a deployment's logs) can see exactly what a startup changed.
    /// </summary>
    [Fact]
    public void Production_startup_logs_the_pending_tenancy_migrations_before_applying_them()
    {
        var loggerFactory = new CapturingLoggerFactory();

        EnsureSchema("Production", loggerFactory: loggerFactory);

        var combined = string.Join('\n', loggerFactory.Messages);
        Assert.Contains("AddBusinessOwnershipModel", combined, StringComparison.Ordinal);
        Assert.Contains("AddBusinessOwnershipToTenantOwnedEntities", combined, StringComparison.Ordinal);
        Assert.Contains("ScopeUniqueConstraintsByBusiness", combined, StringComparison.Ordinal);
    }

    /// <summary>
    /// A migration that fails to apply must stop startup rather than leave the API serving
    /// requests against a partially migrated schema, and the failure must be useful to an
    /// operator: naming the environment and what was pending.
    /// </summary>
    [Fact]
    public void A_failed_migration_prevents_production_startup_and_reports_it()
    {
        // Pre-create a table one of the pending migrations also creates, so applying it fails
        // exactly like a real schema conflict would.
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE Businesses (Id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        var loggerFactory = new CapturingLoggerFactory();

        var exception = Assert.Throws<DatabaseMigrationFailedException>(
            () => EnsureSchema("Production", loggerFactory: loggerFactory));

        Assert.Contains("Production", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddBusinessOwnershipModel", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
        Assert.Contains(loggerFactory.Messages, message => message.Contains("Production", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_in_development_still_applies_migrations_automatically()
    {
        EnsureSchema("Development");

        Assert.True(TenancyTablesExist());
    }

    /// <summary>
    /// An ephemeral non-Production, non-Development environment (an integration test or Staging
    /// database, for example) still refuses pending migrations by default: the override exists so
    /// that choice can be made deliberately per environment, not implied by "not Development".
    /// </summary>
    [Fact]
    public void Startup_outside_development_and_production_refuses_to_apply_pending_migrations_by_default()
    {
        var exception = Assert.Throws<PendingMigrationsException>(() => EnsureSchema("Staging"));

        // The database is untouched: nothing was created on the way to failing.
        Assert.False(TenancyTablesExist());

        // The error has to be actionable at 3am, so it names the environment and the command.
        Assert.Contains("Staging", exception.Message, StringComparison.Ordinal);
        Assert.Contains(DatabaseMigrationArguments.CommandName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/tenant-rollout.md", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch exists for disposable non-Production databases, and its configuration
    /// key is named so that reaching for it in Production is obviously wrong - Production no
    /// longer needs it at all (issue #201), since it migrates automatically regardless.
    /// </summary>
    [Fact]
    public void An_explicit_opt_in_allows_automatic_migration_outside_development()
    {
        EnsureSchema(
            "Staging",
            Configuration((DatabaseSchemaStartup.AllowAutomaticMigrationKey, "true")));

        Assert.True(TenancyTablesExist());
    }

    [Fact]
    public void The_opt_in_key_is_named_so_that_using_it_in_production_is_self_evidently_wrong()
    {
        Assert.Contains("Unsafe", DatabaseSchemaStartup.AllowAutomaticMigrationKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// The concurrency risk issue #201's "Safety constraints" section calls for assessing: two
    /// same-machine callers starting against the same on-disk database at the same instant - the
    /// realistic case being an overlapping restart during a deployment on the single App Service
    /// instance this application is pinned to (see the concurrency note on
    /// <see cref="DatabaseSchemaStartup"/>) - must not race to apply the same migration twice.
    /// Both callers are released from a <see cref="Barrier"/> at the same instant to force genuine
    /// overlap rather than relying on scheduling luck, and use a real file (not the shared
    /// <c>:memory:</c> connection the rest of this fixture uses) because the risk is specifically
    /// about two independent connections to one file, which an in-memory database cannot represent.
    /// </summary>
    [Fact]
    public async Task Concurrent_production_startups_do_not_race_to_apply_the_same_migration_twice()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"inventoryapp-concurrent-migrate-test-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            using var barrier = new Barrier(2);

            Task RunStartup() => Task.Run(() =>
            {
                barrier.SignalAndWait();
                using var db = TestAppDbContext.Unrestricted(options);
                DatabaseSchemaStartup.EnsureSchema(
                    db,
                    Environment("Production"),
                    Configuration(),
                    NullLoggerFactory.Instance);
            });

            var first = RunStartup();
            var second = RunStartup();

            await Task.WhenAll(first, second);

            using var verifyConnection = new SqliteConnection(connectionString);
            verifyConnection.Open();
            Assert.True(TenancyTablesExist(verifyConnection));

            using var verifyDb = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConnection).Options);
            Assert.Empty(verifyDb.Database.GetPendingMigrations());
        }
        finally
        {
            foreach (var path in new[] { databasePath, databasePath + "-journal", databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
