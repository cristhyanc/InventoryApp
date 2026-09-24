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
            options.UseLazyLoadingProxies();
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // Schema work spans every business by definition, so it takes the unrestricted scope
        // explicitly rather than inheriting it.
        await using var db = new AppDbContext(dbOptions, UnscopedBusinessScope.Instance);

        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        Console.WriteLine();
        Console.WriteLine($"Database migration - {(apply ? "APPLY" : "DRY RUN (nothing will be applied)")}");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"Applied migrations : {applied.Count}");
        Console.WriteLine($"Pending migrations : {pending.Count}");
        Console.WriteLine();

        if (pending.Count == 0)
        {
            Console.WriteLine("The schema is already up to date. Nothing to do.");
            Console.WriteLine();
            return 0;
        }

        foreach (var migration in pending)
        {
            Console.WriteLine($"  pending  {migration}");
        }

        Console.WriteLine();

        if (!apply)
        {
            Console.WriteLine(
                "Dry run: nothing was applied. Take a verified backup, then re-run with "
                    + $"{DatabaseMigrationArguments.ApplyFlag}.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine("Applying...");
        await db.Database.MigrateAsync(cancellationToken);

        var remaining = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        Console.WriteLine();
        Console.WriteLine(
            remaining.Count == 0
                ? "Schema applied. No migrations remain pending."
                : $"WARNING: {remaining.Count} migration(s) still pending after apply.");

        // Schema is only half the rollout; say plainly what state the data is in, so an operator
        // does not read "applied" as "finished" and leave callers looking at an empty dataset.
        var readiness = TenantOwnershipReadiness.Inspect(db);
        Console.WriteLine();
        Console.WriteLine(
            readiness.IsReady
                ? "Tenant ownership is bootstrapped; no further action needed."
                : $"Tenant ownership is NOT yet bootstrapped: {readiness.UnassignedRows} unassigned row(s), "
                    + $"{readiness.BusinessCount} business(es) of which {readiness.ActiveBusinessCount} active, "
                    + $"{readiness.UsableMembershipCount} active membership(s) on an active business. "
                    + $"Run `{BusinessBootstrapArguments.CommandName} "
                    + $"{BusinessBootstrapArguments.DryRunFlag}` next. See docs/tenant-rollout.md.");
        Console.WriteLine();

        return remaining.Count == 0 ? 0 : 1;
    }
}
