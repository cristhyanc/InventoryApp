using System.Globalization;
using Inventory.Application.Tenancy;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// The command-line entry point for the business bootstrap (issue #64, checkpoint 3).
///
/// It exists so the backfill is something a human runs on purpose, with its output in front of
/// them, rather than something a deployment performs. The API never invokes it: starting the web
/// host and running this command are mutually exclusive paths through <c>Program.cs</c>.
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- bootstrap-business --dry-run
///   dotnet run --project backend/InventoryApi -- bootstrap-business --apply
/// </code>
///
/// From the deployed application, which is `dotnet publish` output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll bootstrap-business --dry-run
///   dotnet InventoryApi.dll bootstrap-business --apply
/// </code>
///
/// <c>--apply</c> must be typed explicitly; an invocation with neither flag is treated as a dry
/// run, so a half-remembered command cannot mutate data. Passing both flags, or any flag this
/// command does not define, is refused rather than resolved by precedence - see
/// <see cref="BusinessBootstrapArguments"/>.
/// </summary>
public static class BusinessBootstrapCommand
{
    public const string CommandName = BusinessBootstrapArguments.CommandName;

    public static bool Matches(string[] args) => BusinessBootstrapArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!BusinessBootstrapArguments.TryParse(args, out var apply, out var argumentError))
        {
            Console.WriteLine();
            Console.WriteLine($"Business bootstrap - REFUSED: {argumentError}");
            Console.WriteLine();
            return 1;
        }

        var dryRun = !apply;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseLazyLoadingProxies();
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=inventory.db");
        });

        var options = new BusinessBootstrapOptions();
        builder.Configuration.GetSection(BusinessBootstrapOptions.SectionName).Bind(options);

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // The backfill works across the tenant boundary on purpose, so it takes the unrestricted
        // scope explicitly. This is one of the two places in the codebase that does.
        await using var db = new AppDbContext(dbOptions, UnscopedBusinessScope.Instance);

        var bootstrapper = new BusinessBootstrapper(db, options, TimeProvider.System);
        var result = await bootstrapper.RunAsync(dryRun, cancellationToken);

        Write(result);

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Prints the full before/after evidence. This output is the artefact a human reviews before
    /// authorising the apply, and again after it, so it reports measured numbers rather than a
    /// summary judgement - and never an Entra identifier.
    /// </summary>
    private static void Write(BusinessBootstrapResult result)
    {
        var mode = result.DryRun ? "DRY RUN (no changes committed)" : "APPLY";
        Console.WriteLine();
        Console.WriteLine($"Business bootstrap - {mode}");
        Console.WriteLine(new string('-', 78));

        if (!result.Succeeded)
        {
            Console.WriteLine($"FAILED ({result.Outcome}): {result.Message}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"Run id            : {result.RunId}");
        Console.WriteLine($"Business id       : {result.BusinessId}");
        Console.WriteLine($"Business created  : {(result.BusinessCreated ? "yes" : "no (reused existing)")}");
        Console.WriteLine($"Memberships added : {result.MembershipsCreated}");
        Console.WriteLine($"Rows assigned     : {result.TotalRowsAssigned}");
        Console.WriteLine();

        Console.WriteLine($"{"Table",-40}{"Rows",8}{"Unassigned",12}{"Assigned",10}{"Left",6}");
        foreach (var table in result.Tables)
        {
            Console.WriteLine(
                $"{table.TableName,-40}{table.TotalRowsAfter,8}{table.UnassignedRowsBefore,12}"
                    + $"{table.RowsAssigned,10}{table.UnassignedRowsAfter,6}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"Financial / inventory total",-40}{"Before",18}{"After",18}  OK");
        foreach (var total in result.Totals)
        {
            Console.WriteLine(
                $"{total.Name,-40}{Format(total.Before),18}{Format(total.After),18}  "
                    + $"{(total.IsPreserved ? "yes" : "NO")}");
        }

        Console.WriteLine();
        Console.WriteLine(result.Message);
        Console.WriteLine();
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
