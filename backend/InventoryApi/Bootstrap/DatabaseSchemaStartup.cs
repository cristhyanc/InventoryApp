using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Thrown when the API starts against a database whose schema is behind the code, in an
/// environment where applying migrations is a human's decision rather than the application's.
/// </summary>
public sealed class PendingMigrationsException : Exception
{
    public PendingMigrationsException(string message) : base(message)
    {
    }

    public PendingMigrationsException() : this("The database has pending migrations.")
    {
    }

    public PendingMigrationsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the API attempts to apply pending migrations automatically at startup and the
/// attempt fails (issue #201). Startup must not continue serving requests against a
/// partially-migrated or otherwise incompatible schema, so this is fatal.
/// </summary>
public sealed class DatabaseMigrationFailedException : Exception
{
    public DatabaseMigrationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Decides whether starting the API may change the database schema (issue #64, revised by issue
/// #201).
///
/// It previously called <c>Database.Migrate()</c> unconditionally, which meant a deployment
/// applied whatever migrations the build happened to contain - including the high-risk tenancy
/// ones, and the NayaxSales table rebuild that rewrites every sale row. Issue #64 then required
/// the opposite: production schema changes applied and verified under human control, with startup
/// refusing outright rather than touching the schema. That cost every Production deployment a
/// mandatory, separate manual step. Issue #201 restores automatic migration for normal Production
/// startup, now that the tenancy rollout it protected is long complete, while keeping the
/// fail-closed behaviour issue #64 introduced: a failed migration still stops the application
/// rather than letting it serve requests against a schema its code does not match, and the
/// explicit <c>migrate-database</c> command remains available for diagnostics and manual use.
///
/// The rule now depends on the environment:
/// <list type="bullet">
///   <item><b>Development, <c>Testing</c>, and Production</b> all apply pending migrations
///   automatically at startup.</item>
///   <item><b>Everywhere else</b> (an ephemeral Staging-like environment, for example) startup
///   applies nothing unless the unsafe override is set, and fails closed with an operator-facing
///   error naming the exact command to run when migrations are pending and the override is not
///   set.</item>
/// </list>
///
/// <b>Concurrent startup instances.</b> The realistic risk is not two permanently co-running
/// instances - the App Service plan for this application is pinned to a single instance with no
/// scale-out while SQLite remains the store (see docs/architecture.md § SQLite operating
/// assumptions), which this policy does not change - but a same-machine restart briefly
/// overlapping the previous process during a deployment. No extra locking is added for this: EF
/// Core's <c>Database.Migrate()</c> re-reads the applied-migrations history at the moment it runs
/// rather than trusting an earlier snapshot, and SQLite allows only one writer at a time (the same
/// single-writer lock documented in docs/architecture.md), so a second overlapping caller blocks
/// on the existing 30-second busy timeout until the first finishes and then finds nothing left
/// pending, rather than re-applying and colliding with what the first already created. This is
/// exercised directly by
/// <c>DatabaseSchemaStartupTests.Concurrent_production_startups_do_not_race_to_apply_the_same_migration_twice</c>
/// against a real on-disk database with two genuinely concurrent callers.
/// </summary>
public static class DatabaseSchemaStartup
{
    /// <summary>
    /// Opt-in override for an environment that is not named Development but is still
    /// disposable - an ephemeral integration-test or Staging database, for example.
    ///
    /// It has no effect in Production: Production applies pending migrations automatically
    /// regardless of this setting (issue #201).
    /// </summary>
    public const string AllowAutomaticMigrationKey = "Database:AllowAutomaticMigrationUnsafeOutsideDevelopment";

    /// <summary>
    /// Applies or verifies the schema according to the rule above.
    /// </summary>
    /// <exception cref="PendingMigrationsException">
    /// Migrations are pending and this environment does not apply them automatically.
    /// </exception>
    /// <exception cref="DatabaseMigrationFailedException">
    /// This environment applies migrations automatically and the attempt failed.
    /// </exception>
    public static void EnsureSchema(
        AppDbContext db,
        IHostEnvironment environment,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(DatabaseSchemaStartup).FullName!);

        if (MayMigrateAutomatically(environment, configuration))
        {
            ApplyPendingMigrations(db, environment, logger);
            return;
        }

        var pending = db.Database.GetPendingMigrations().ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation(
                "Database schema is up to date; startup applied no migrations ({Environment} environment).",
                environment.EnvironmentName);
            return;
        }

        var message =
            $"The database has {pending.Count} pending migration(s) and this environment "
            + $"({environment.EnvironmentName}) does not apply them automatically. Schema changes are "
            + "applied by a human, under review, with a verified backup in hand - deploying the API is "
            + "not a way to change the schema. Pending: "
            + string.Join(", ", pending)
            + $". Apply them with `{DatabaseMigrationArguments.CommandName} {DatabaseMigrationArguments.ApplyFlag}` "
            + $"(or inspect with `{DatabaseMigrationArguments.CommandName} {DatabaseMigrationArguments.DryRunFlag}`). "
            + "See docs/tenant-rollout.md.";

        logger.LogCritical("{Message}", message);

        throw new PendingMigrationsException(message);
    }

    private static bool MayMigrateAutomatically(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || environment.IsEnvironment("Testing")
        // Issue #201: normal Production startup applies pending migrations automatically, the
        // same as Development/Testing, instead of refusing to start.
        || environment.IsProduction()
        || configuration.GetValue<bool>(AllowAutomaticMigrationKey);

    /// <summary>
    /// Applies whatever is pending and fails closed with a clear error if the attempt does not
    /// succeed. See the concurrency note on this type for why no explicit lock guards this call.
    /// </summary>
    private static void ApplyPendingMigrations(AppDbContext db, IHostEnvironment environment, ILogger logger)
    {
        var pending = db.Database.GetPendingMigrations().ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation(
                "Database schema is up to date; startup applied no migrations ({Environment} environment).",
                environment.EnvironmentName);
            return;
        }

        logger.LogInformation(
            "Applying {Count} pending database migration(s) automatically ({Environment} environment): {Migrations}",
            pending.Count,
            environment.EnvironmentName,
            string.Join(", ", pending));

        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            var message =
                $"Applying {pending.Count} pending database migration(s) failed "
                + $"({environment.EnvironmentName} environment): {ex.Message}. The application will not "
                + "start against a schema its code does not match. Pending migration(s) were: "
                + string.Join(", ", pending)
                + ".";

            logger.LogCritical(ex, "{Message}", message);

            throw new DatabaseMigrationFailedException(message, ex);
        }

        logger.LogInformation(
            "Database migration completed automatically; schema is up to date ({Environment} environment).",
            environment.EnvironmentName);
    }
}
