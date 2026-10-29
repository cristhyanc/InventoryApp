using System.Globalization;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Grants the grandfathered Pro plan to every business that was already trading, and records the
/// completion marker plan enforcement waits for (issue #966).
///
/// <para>It is a human-invoked command for the reason the others are: it writes an entitlement every
/// existing business then depends on, and the plan rollout deliberately refuses to restrict anybody
/// until a person has run it and reviewed its output. Starting the web host and running this command
/// are mutually exclusive paths through <c>Program.cs</c>, and this one applies no migration.</para>
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- grandfather-business-plans --dry-run
///   dotnet run --project backend/InventoryApi -- grandfather-business-plans --apply
/// </code>
///
/// From the deployed application, which is <c>dotnet publish</c> output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll grandfather-business-plans --dry-run
///   dotnet InventoryApi.dll grandfather-business-plans --apply
/// </code>
///
/// <para><c>--apply</c> must be typed explicitly; an invocation with neither flag is a dry run, and a
/// dry run writes nothing at all - not a grant, and not the cutoff. The run takes no cutoff, plan,
/// date or business on the command line: the first apply fixes the cutoff from the clock, every later
/// run reuses it, and which businesses are eligible is
/// <see cref="GrandfatheringEligibilityPolicy"/>'s answer. Re-running after it completed changes
/// nothing and says so.</para>
///
/// <para>Exit code: <c>0</c> when the run succeeded, including a dry run and a rerun after
/// completion; <c>1</c> for a refused argument list or a refused run - so an apply can be gated on a
/// clean dry run.</para>
/// </summary>
public static class BusinessPlanGrandfatheringCommand
{
    public const string CommandName = BusinessPlanGrandfatheringArguments.CommandName;

    public static bool Matches(string[] args) => BusinessPlanGrandfatheringArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!BusinessPlanGrandfatheringArguments.TryParse(args, out var apply, out var argumentError))
        {
            return Refuse(Console.Out, argumentError);
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
        IClock clock = new SystemClock();

        // Deliberately *not* an unrestricted context, for the reason `migrate-nayax-connection` and
        // `resolve-nayax-sale-products` are not (AGENTS.md § Tenant ownership and data isolation).
        // The Businesses table carries no tenant query filter and the grandfathering record is a
        // platform row that no filter applies to, so a denied context reads both while reaching no
        // tenant-owned row; every grant is then written through a context scoped to the one business
        // it belongs to.
        await using var tenancyDb = new AppDbContext(dbOptions, new BusinessScope());

        var scopedContexts = new List<AppDbContext>();

        try
        {
            var grandfatherer = new BusinessPlanGrandfatherer(
                tenancyDb,
                new EfSubscriptionGrandfatheringStore(tenancyDb),
                businessId =>
                {
                    var scopedDb = new AppDbContext(dbOptions, ResolvedScopeFor(businessId));
                    scopedContexts.Add(scopedDb);
                    return new EfManualPlanGrantStore(scopedDb);
                },
                clock);

            var report = await grandfatherer.RunAsync(dryRun: !apply, cancellationToken);

            Write(report, Console.Out);

            return report.Succeeded ? 0 : 1;
        }
        finally
        {
            foreach (var scopedDb in scopedContexts)
            {
                await scopedDb.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The scope a request for this business would carry. The business id comes from the database
    /// this command read it in, never from the command line: there is no <c>--business-id</c> flag,
    /// and adding one would make an entitlement something an operator types.
    /// </summary>
    private static IBusinessScope ResolvedScopeFor(int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return scope;
    }

    private static int Refuse(TextWriter output, string reason)
    {
        output.WriteLine();
        output.WriteLine($"Business plan grandfathering - REFUSED: {reason}");
        output.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prints what an operator reviews before authorising an apply, and again afterwards.
    ///
    /// <para>Nothing printed here is a secret: business keys and lifecycle decisions are the
    /// platform's own operational data, and no business name, contact address, member identity,
    /// credential or amount of money is shown. The plan is named because it is the same plan for
    /// every eligible business.</para>
    /// </summary>
    public static void Write(BusinessPlanGrandfatheringReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine(
            "Business plan grandfathering - "
                + (report.DryRun ? "DRY RUN (nothing will be written)" : "APPLY"));
        output.WriteLine(new string('-', 78));

        if (report.Refusal is { } refusal)
        {
            output.WriteLine($"FAILED ({report.Outcome}): {refusal}");
            output.WriteLine();
            return;
        }

        output.WriteLine(
            $"Cutoff (UTC)         : {Describe(report.CutoffUtc)}"
                + (report.CutoffWasAlreadyRecorded
                    ? "   <- already recorded; this run grants by it and never moves it"
                    : report.DryRun
                        ? "   <- not recorded yet; an apply would fix this instant"
                        : "   <- recorded by this run"));
        output.WriteLine($"Granted plan         : {GrandfatheringEligibilityPolicy.GrandfatheredPlan}");

        if (report.Outcome == BusinessPlanGrandfatheringOutcome.AlreadyComplete)
        {
            output.WriteLine();
            output.WriteLine(
                "The grandfathering step has already completed. Nothing was written, no grant was "
                    + "added or changed, and the completion marker is unchanged.");
            output.WriteLine();
            return;
        }

        output.WriteLine($"Eligible businesses  : {Count(report.Eligible.Count)}");
        output.WriteLine(
            $"  {(report.DryRun ? "would be granted" : "granted now     ")}   : "
                + Count(report.DryRun
                    ? report.Eligible.Count - report.AlreadyHeld.Count
                    : report.Granted.Count));
        output.WriteLine($"  already held      : {Count(report.AlreadyHeld.Count)}");
        output.WriteLine($"Not eligible         : {Count(report.NotEligible.Count)}");
        output.WriteLine();

        foreach (var entry in report.Businesses)
        {
            output.WriteLine(
                $"  business {Count(entry.BusinessId)}: {Describe(entry, report.DryRun)}");
        }

        output.WriteLine();
        output.WriteLine(
            report.CompletionMarkerWritten
                ? "Completion marker written: plan enforcement may now take effect where the "
                    + "rollout setting is on."
                : "Completion marker NOT written: nothing is enforced for any business.");
        output.WriteLine();
    }

    /// <summary>
    /// One business's line, which says what happened to it rather than only what it is: an operator
    /// reading an apply has to be able to tell a grant that was written from one that was already
    /// there, and a dry run from either.
    /// </summary>
    private static string Describe(BusinessPlanGrandfatheringEntry entry, bool dryRun) =>
        entry.Decision switch
        {
            GrandfatheringEligibilityPolicy.Decision.Eligible when entry.AlreadyHeld =>
                "eligible - already holds the grandfathered grant, left untouched",
            GrandfatheringEligibilityPolicy.Decision.Eligible when dryRun =>
                "eligible - would be granted the grandfathered plan",
            GrandfatheringEligibilityPolicy.Decision.Eligible =>
                "eligible - granted the grandfathered plan",
            GrandfatheringEligibilityPolicy.Decision.CreatedAtOrAfterCutoff =>
                "not eligible - created at or after the cutoff, so it starts on Free",
            GrandfatheringEligibilityPolicy.Decision.NotTradingAtCutoff =>
                "not eligible - not trading at the cutoff (a pending or rejected application), so "
                    + "it starts on Free",
            GrandfatheringEligibilityPolicy.Decision.DecidedAtOrAfterCutoff =>
                "not eligible - its application was decided at or after the cutoff, so it was still "
                    + "pending then and starts on Free",
            _ => $"not eligible - {entry.Decision}",
        };

    private static string Describe(DateTime? instant) =>
        instant is null ? "none" : instant.Value.ToString("u", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
