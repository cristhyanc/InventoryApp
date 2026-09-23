using InventoryApi.Bootstrap;
using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Startup must not change a production schema (issue #64).
///
/// Issue #64 requires the migration to be applied and verified under human control. The tenancy
/// migrations are high risk - the uniqueness one rebuilds the whole NayaxSales table - so a
/// deployment that silently applied whatever the build happened to contain would defeat the
/// review the rollout depends on.
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

    private void EnsureSchema(string environmentName, IConfiguration? configuration = null)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        DatabaseSchemaStartup.EnsureSchema(
            db,
            Environment(environmentName),
            configuration ?? Configuration(),
            NullLoggerFactory.Instance);
    }

    private bool TenancyTablesExist()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Businesses';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// The blocker this change exists to close: starting the API against a production database
    /// with pending migrations must refuse, not migrate.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Startup_outside_development_refuses_to_apply_pending_migrations(string environmentName)
    {
        var exception = Assert.Throws<PendingMigrationsException>(() => EnsureSchema(environmentName));

        // The database is untouched: nothing was created on the way to failing.
        Assert.False(TenancyTablesExist());

        // The error has to be actionable at 3am, so it names the environment and the command.
        Assert.Contains(environmentName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(DatabaseMigrationArguments.CommandName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/tenant-rollout.md", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pending list is named so a reviewer can see exactly which high-risk migrations a
    /// deployment was about to apply.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_pending_tenancy_migrations()
    {
        var exception = Assert.Throws<PendingMigrationsException>(() => EnsureSchema("Production"));

        Assert.Contains("AddBusinessOwnershipModel", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddBusinessOwnershipToTenantOwnedEntities", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ScopeUniqueConstraintsByBusiness", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_in_development_still_applies_migrations_automatically()
    {
        EnsureSchema("Development");

        Assert.True(TenancyTablesExist());
    }

    /// <summary>
    /// An already-migrated production database starts normally: the rule is "do not change the
    /// schema", not "refuse to run".
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
    /// The escape hatch exists for disposable non-Production databases, and its configuration
    /// key is named so that reaching for it in production is obviously wrong.
    /// </summary>
    [Fact]
    public void An_explicit_opt_in_allows_automatic_migration_outside_development()
    {
        EnsureSchema(
            "Staging",
            Configuration((DatabaseSchemaStartup.AllowAutomaticMigrationKey, "true")));

        Assert.True(TenancyTablesExist());
    }

    /// <summary>
    /// The regression this pins down: Production must never migrate automatically, and the unsafe
    /// override must not be able to buy its way past that.
    ///
    /// A configuration value is the wrong thing to stake production data on. It can arrive from a
    /// deployment template, an environment variable, or an App Service setting copied from
    /// staging - none of which is the human review issue #64 requires before the tenancy
    /// migrations run. So Production is decided by environment name alone.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    public void Production_never_migrates_automatically_even_with_the_unsafe_override(string overrideValue)
    {
        var exception = Assert.Throws<PendingMigrationsException>(() => EnsureSchema(
            "Production",
            Configuration((DatabaseSchemaStartup.AllowAutomaticMigrationKey, overrideValue))));

        // Not one migration was applied on the way to failing.
        Assert.False(TenancyTablesExist());
        Assert.Contains(DatabaseMigrationArguments.CommandName, exception.Message, StringComparison.Ordinal);

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Database.GetAppliedMigrations());
    }

    [Fact]
    public void The_opt_in_key_is_named_so_that_using_it_in_production_is_self_evidently_wrong()
    {
        Assert.Contains("Unsafe", DatabaseSchemaStartup.AllowAutomaticMigrationKey, StringComparison.Ordinal);
    }
}
