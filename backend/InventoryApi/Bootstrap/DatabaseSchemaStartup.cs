using InventoryApi.Data;
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
/// Decides whether starting the API may change the database schema (issue #64).
///
/// It previously called <c>Database.Migrate()</c> unconditionally, which meant a deployment
/// applied whatever migrations the build happened to contain - including the high-risk tenancy
/// ones, and the NayaxSales table rebuild that rewrites every sale row. Issue #64 requires the
/// opposite: "apply and verify migration under human control".
///
/// So the rule now depends on the environment:
/// <list type="bullet">
///   <item><b>Development and testing</b> keep automatic migration, where a throwaway database
///   is the point and the convenience costs nothing.</item>
///   <item><b>Everywhere else</b>, including Production, startup applies nothing. If migrations
///   are pending it fails closed with an operator-facing error naming the exact command to run;
///   deploying the API is no longer a way to change the schema.</item>
/// </list>
///
/// Failing to start is the correct response rather than an over-reaction: the alternative is an
/// API serving requests against a schema its code does not match, which for tenant ownership
/// means queries filtering on a column that may not exist yet.
/// </summary>
public static class DatabaseSchemaStartup
{
    /// <summary>
    /// Opt-in override, for an environment that is not named Development but is still
    /// disposable - an ephemeral integration-test database, for example. It must never be set in
    /// production, and the name says so.
    /// </summary>
    public const string AllowAutomaticMigrationKey = "Database:AllowAutomaticMigrationUnsafeOutsideDevelopment";

    /// <summary>
    /// Applies or verifies the schema according to the rule above.
    /// </summary>
    /// <exception cref="PendingMigrationsException">
    /// Migrations are pending and this environment does not apply them automatically.
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
            logger.LogInformation(
                "Applying database migrations automatically ({Environment} environment).",
                environment.EnvironmentName);
            db.Database.Migrate();
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
        || configuration.GetValue<bool>(AllowAutomaticMigrationKey);
}
