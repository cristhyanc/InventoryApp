using Inventory.Application.Tenancy;
using InventoryApi.Data;
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
    IReadOnlyList<string> PendingBefore,
    bool Applied,
    IReadOnlyList<string> PendingAfter,
    TenantOwnershipReadinessState? Readiness)
{
    public bool WasUpToDate => PendingBefore.Count == 0;

    public bool FullyApplied => PendingAfter.Count == 0;

    /// <summary>
    /// The process exit code for this outcome: non-zero only when an apply ran and still left
    /// migrations pending. A dry run and an already up-to-date database both succeed.
    /// </summary>
    public int ExitCode => Applied && !FullyApplied ? 1 : 0;
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
        var pendingBefore = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        output.WriteLine();
        output.WriteLine($"Database migration - {(apply ? "APPLY" : "DRY RUN (nothing will be applied)")}");
        output.WriteLine(new string('-', 78));
        output.WriteLine($"Applied migrations : {appliedBefore.Count}");
        output.WriteLine($"Pending migrations : {pendingBefore.Count}");
        output.WriteLine();

        if (pendingBefore.Count == 0)
        {
            output.WriteLine("The schema is already up to date. Nothing to do.");
            output.WriteLine();
            return new DatabaseMigrationOutcome(appliedBefore, pendingBefore, Applied: false, pendingBefore, Readiness: null);
        }

        foreach (var migration in pendingBefore)
        {
            output.WriteLine($"  pending  {migration}");
        }

        output.WriteLine();

        if (!apply)
        {
            output.WriteLine(
                "Dry run: nothing was applied. Take a verified backup, then re-run with "
                    + $"{DatabaseMigrationArguments.ApplyFlag}.");
            output.WriteLine();
            return new DatabaseMigrationOutcome(appliedBefore, pendingBefore, Applied: false, pendingBefore, Readiness: null);
        }

        output.WriteLine("Applying...");

        // Flush before handing control to EF: if the migration throws, or the operator loses the
        // session mid-apply, everything above must already be on their screen.
        output.Flush();

        await db.Database.MigrateAsync(cancellationToken);
        var pendingAfter = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        output.WriteLine();
        output.WriteLine(
            pendingAfter.Count == 0
                ? "Schema applied. No migrations remain pending."
                : $"WARNING: {pendingAfter.Count} migration(s) still pending after apply.");

        // Schema is only half the rollout; readiness reflects what state the data is actually in,
        // so an operator does not read "applied" as "finished" and leave callers looking at an
        // empty dataset. Only computed after a real apply - it depends on the schema being
        // current, and a dry run or an already-up-to-date database has nothing new to report.
        var readiness = TenantOwnershipReadiness.Inspect(db);

        output.WriteLine();
        output.WriteLine(
            readiness.IsReady
                ? "Tenant ownership is bootstrapped; no further action needed."
                : $"Tenant ownership is NOT yet bootstrapped: {readiness.UnassignedRows} unassigned row(s), "
                    + $"{readiness.BusinessCount} business(es) of which {readiness.ActiveBusinessCount} active, "
                    + $"{readiness.UsableMembershipCount} active membership(s) on an active business. "
                    + $"Run `{BusinessBootstrapArguments.CommandName} "
                    + $"{BusinessBootstrapArguments.DryRunFlag}` next. See docs/tenant-rollout.md.");
        output.WriteLine();

        return new DatabaseMigrationOutcome(appliedBefore, pendingBefore, Applied: true, pendingAfter, readiness);
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
