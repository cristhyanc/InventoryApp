using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>migrate-database</c> command line. Same strictness as the bootstrap command:
/// this one changes schema on a production database, so an ambiguous argument list is refused
/// rather than resolved by precedence.
/// </summary>
public static class DatabaseMigrationArguments
{
    public const string CommandName = "migrate-database";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="apply">True only when <c>--apply</c> was given on its own.</param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out bool apply, out string error)
    {
        apply = false;
        error = string.Empty;

        var sawApply = false;
        var sawDryRun = false;

        foreach (var argument in args.Skip(1))
        {
            if (string.Equals(argument, ApplyFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawApply = true;
            }
            else if (string.Equals(argument, DryRunFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawDryRun = true;
            }
            else
            {
                error = $"Unrecognised argument '{argument}'. "
                    + $"Usage: {CommandName} [{DryRunFlag} | {ApplyFlag}]";
                return false;
            }
        }

        if (sawApply && sawDryRun)
        {
            error = $"{ApplyFlag} and {DryRunFlag} are mutually exclusive. "
                + "Pass exactly one, or neither to list pending migrations.";
            return false;
        }

        apply = sawApply;
        return true;
    }
}

/// <summary>
/// What <see cref="DatabaseMigrator.RunAsync"/> found and did. Separated from
/// <see cref="DatabaseMigrationCommand"/> so the detection/apply behaviour is verifiable against a
/// real relational database without going through process argument parsing or building a
/// <c>WebApplicationBuilder</c>.
/// </summary>
/// <param name="AppliedBefore">Migrations already applied before this run.</param>
/// <param name="UnknownApplied">
/// Applied migrations this build does not contain (issue #1299): the database's schema is newer
/// than the code, which pending migrations alone never reveal.
/// </param>
/// <param name="PendingBefore">Migrations pending before this run.</param>
/// <param name="Applied">True only when this run actually called <c>Migrate</c>.</param>
/// <param name="PendingAfter">
/// Migrations still pending after this run. Equal to <paramref name="PendingBefore"/> when
/// nothing was applied (dry run, or already up to date).
/// </param>
/// <param name="Readiness">
/// Tenant ownership readiness, computed only after a successful apply - checking it beforehand
/// or on a dry run would not reflect the schema the readiness check itself depends on.
/// </param>
public sealed record DatabaseMigrationOutcome(
    IReadOnlyList<string> AppliedBefore,
    IReadOnlyList<string> UnknownApplied,
    IReadOnlyList<string> PendingBefore,
    bool Applied,
    IReadOnlyList<string> PendingAfter,
    TenantOwnershipReadinessState? Readiness)
{
    public bool WasUpToDate => PendingBefore.Count == 0;

    public bool FullyApplied => PendingAfter.Count == 0;

    /// <summary>True when the database's schema is newer than this build (issue #1299).</summary>
    public bool SchemaIsNewerThanThisBuild => UnknownApplied.Count > 0;

    /// <summary>
    /// The process exit code for this outcome: non-zero when an apply ran and still left migrations
    /// pending, and non-zero whenever the schema is newer than this build - a database this build
    /// does not understand is never a success, which is why a dry run against one no longer reports
    /// "already up to date" and exits 0 (issue #1299). An up-to-date database this build does
    /// understand still succeeds in both modes.
    /// </summary>
    public int ExitCode => SchemaIsNewerThanThisBuild || (Applied && !FullyApplied) ? 1 : 0;
}

/// <summary>
/// The detection/apply behaviour behind the <c>migrate-database</c> command, against an already
/// constructed <see cref="AppDbContext"/> so tests can drive it directly with a relational SQLite
/// database instead of a process argument list.
///
/// It writes the operator-facing report itself rather than returning it for the caller to print,
/// because the order matters and is part of the behaviour: the summary, the pending migration
/// names and "Applying..." must reach the operator <i>before</i> the migration starts, so that an
/// apply which fails part-way still leaves them looking at what was attempted.
///
/// Since issue #1299 it also reports the applied migrations this build does <i>not</i> contain -
/// the database's schema being newer than the code, which no count of pending migrations reveals -
/// and that report makes the run exit non-zero instead of saying "already up to date". How
/// migrations are applied is unchanged: an <c>--apply</c> against such a database still applies
/// exactly what is pending, with the finding on the operator's screen first.
/// </summary>
public static class DatabaseMigrator
{
    /// <param name="db">An unrestricted context - schema work spans every business.</param>
    /// <param name="apply">True to migrate; false to report what is pending and stop.</param>
    /// <param name="output">Where the operator-facing report is written, normally the console.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    public static async Task<DatabaseMigrationOutcome> RunAsync(
        AppDbContext db,
        bool apply,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var appliedBefore = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var unknownApplied = UnknownAppliedMigrations.In(appliedBefore, db.Database.GetMigrations());
        var pendingBefore = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        await output.WriteLineAsync();
        await output.WriteLineAsync(
            $"Database migration - {(apply ? "APPLY" : "DRY RUN (nothing will be applied)")}");
        await output.WriteLineAsync(new string('-', 78));
        await output.WriteLineAsync($"Applied migrations : {appliedBefore.Count}");
        await output.WriteLineAsync($"Pending migrations : {pendingBefore.Count}");

        if (unknownApplied.Count > 0)
        {
            await output.WriteLineAsync($"Unknown applied    : {unknownApplied.Count}");
        }

        await output.WriteLineAsync();

        // Issue #1299: an applied id this build does not contain is reported before anything else
        // is said about pending work, because it changes what the operator should do next - and on
        // an apply it has to be on their screen before `Migrate` is called.
        if (unknownApplied.Count > 0)
        {
            foreach (var migration in unknownApplied)
            {
                await output.WriteLineAsync($"  unknown  {migration}");
            }

            await output.WriteLineAsync();
            await output.WriteLineAsync(UnknownAppliedMigrations.Summarise(unknownApplied));
            await output.WriteLineAsync(UnknownAppliedMigrations.RecoveryGuidance);
            await output.WriteLineAsync();
        }

        if (pendingBefore.Count == 0)
        {
            // "Already up to date" would be false here: this build is behind the database, which is
            // the exact mistake the unknown-applied report above exists to stop (issue #1299).
            if (unknownApplied.Count == 0)
            {
                await output.WriteLineAsync("The schema is already up to date. Nothing to do.");
                await output.WriteLineAsync();
            }

            return new DatabaseMigrationOutcome(
                appliedBefore,
                unknownApplied,
                pendingBefore,
                Applied: false,
                pendingBefore,
                Readiness: null);
        }

        foreach (var migration in pendingBefore)
        {
            await output.WriteLineAsync($"  pending  {migration}");
        }

        await output.WriteLineAsync();

        if (!apply)
        {
            await output.WriteLineAsync(
                unknownApplied.Count > 0
                    ? "Dry run: nothing was applied. Resolve the unknown applied migration(s) above "
                        + "first; this build is not the one to apply anything to this database."
                    : "Dry run: nothing was applied. Take a verified backup, then re-run with "
                        + $"{DatabaseMigrationArguments.ApplyFlag}.");
            await output.WriteLineAsync();
            return new DatabaseMigrationOutcome(
                appliedBefore,
                unknownApplied,
                pendingBefore,
                Applied: false,
                pendingBefore,
                Readiness: null);
        }

        await output.WriteLineAsync("Applying...");

        // Flush before handing control to EF: if the migration throws, or the operator loses the
        // session mid-apply, everything above must already be on their screen. Unconditionally, so
        // a cancellation cannot be the reason the operator is left without the report - hence
        // CancellationToken.None rather than the caller's token, stated explicitly so the opt-out
        // reads as the decision it is rather than as a forgotten argument (SonarCloud S8949).
        await output.FlushAsync(CancellationToken.None);

        await db.Database.MigrateAsync(cancellationToken);
        var pendingAfter = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        await output.WriteLineAsync();
        await output.WriteLineAsync(
            pendingAfter.Count == 0
                ? "Schema applied. No migrations remain pending."
                : $"WARNING: {pendingAfter.Count} migration(s) still pending after apply.");

        // Schema is only half the rollout; readiness reflects what state the data is actually in,
        // so an operator does not read "applied" as "finished" and leave callers looking at an
        // empty dataset. Only computed after a real apply - it depends on the schema being
        // current, and a dry run or an already-up-to-date database has nothing new to report.
        var readiness = TenantOwnershipReadiness.Inspect(db);

        await output.WriteLineAsync();
        await output.WriteLineAsync(
            readiness.IsReady
                ? "Tenant ownership is bootstrapped; no further action needed."
                : $"Tenant ownership is NOT yet bootstrapped: {readiness.UnassignedRows} unassigned row(s), "
                    + $"{readiness.BusinessCount} business(es) of which {readiness.ActiveBusinessCount} active, "
                    + $"{readiness.UsableMembershipCount} active membership(s) on an active business. "
                    + $"Run `{BusinessBootstrapArguments.CommandName} "
                    + $"{BusinessBootstrapArguments.DryRunFlag}` next. See docs/tenant-rollout.md.");
        await output.WriteLineAsync();

        return new DatabaseMigrationOutcome(
            appliedBefore,
            unknownApplied,
            pendingBefore,
            Applied: true,
            pendingAfter,
            readiness);
    }
}

/// <summary>
/// Applies schema migrations as a deliberate, human-invoked step (issue #64).
///
/// This is the supported way to change a production schema now that startup refuses to. It ships
/// inside the published application, so an operator runs it where the application already runs
/// and against the connection string that environment already holds - no EF tooling and no copy
/// of the production connection string on someone's laptop.
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- migrate-database --dry-run
///   dotnet run --project backend/InventoryApi -- migrate-database --apply
/// </code>
///
/// From the deployed application, which is `dotnet publish` output and has no SDK or sources -
/// `dotnet run --project` is not available there:
/// <code>
///   dotnet InventoryApi.dll migrate-database --dry-run
///   dotnet InventoryApi.dll migrate-database --apply
/// </code>
///
/// Schema migrations never assign tenant ownership or perform the business backfill; that
/// remains the separate <c>bootstrap-business</c> step. They are not, however, free of data
/// movement: some rebuild tables and copy every row across - the NayaxSales re-key does exactly
/// that - which is precisely why applying them in production stays human-controlled and why the
/// dry run above exists.
/// </summary>
public static class DatabaseMigrationCommand
{
    public const string CommandName = DatabaseMigrationArguments.CommandName;

    public static bool Matches(string[] args) => DatabaseMigrationArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DatabaseMigrationArguments.TryParse(args, out var apply, out var argumentError))
        {
            Console.WriteLine();
            Console.WriteLine($"Database migration - REFUSED: {argumentError}");
            Console.WriteLine();
            return 1;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // Schema work spans every business by definition, so it takes the unrestricted scope
        // explicitly rather than inheriting it.
        await using var db = new AppDbContext(dbOptions, UnscopedBusinessScope.Instance);

        var outcome = await DatabaseMigrator.RunAsync(db, apply, Console.Out, cancellationToken);

        return outcome.ExitCode;
    }
}
