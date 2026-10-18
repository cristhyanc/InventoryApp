using System.Globalization;
using Inventory.Application.SaleProductResolution;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Lists the stored sales that carry no local product, and applies an operator's audited decision
/// about them (issue #758, parent #329 decision A2c).
///
/// <para>It is a human-invoked command for the same reason the others are: it writes a financial
/// attribution, and the release deliberately refuses to start while a sale that is costed today
/// would be left unresolved. Starting the web host and running this command are mutually exclusive
/// paths through <c>Program.cs</c>, and this one applies no migration - which is what lets an
/// operator record the decision the failure gate is waiting for while the API is refusing to come
/// up.</para>
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- resolve-nayax-sale-products --dry-run
///   dotnet run --project backend/InventoryApi -- resolve-nayax-sale-products \
///       --decide 4821=5001 --decided-by "A. Operator" --apply
/// </code>
///
/// From the deployed application, which is <c>dotnet publish</c> output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll resolve-nayax-sale-products --dry-run
///   dotnet InventoryApi.dll resolve-nayax-sale-products --decide 4821=5001 --decided-by "A. Operator" --apply
/// </code>
///
/// <c>--apply</c> must be typed explicitly; an invocation with neither flag is a dry run, and a dry
/// run writes nothing at all. A decision names the sale by its own local key, which the dry run
/// prints: the business is the one the stored sale belongs to, never a value typed on the command
/// line. Re-running an applied decision changes nothing, because a sale that already has a product
/// is no longer unresolved.
///
/// Exit code: <c>0</c> when the run succeeded, including a dry run and an idempotent re-run;
/// <c>1</c> for a refused argument list or any refused decision, so an apply can be gated on a
/// clean dry run.
/// </summary>
public static class NayaxSaleProductResolutionCommand
{
    public const string CommandName = NayaxSaleProductResolutionArguments.CommandName;

    public static bool Matches(string[] args) => NayaxSaleProductResolutionArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!NayaxSaleProductResolutionArguments.TryParse(
                args, out var apply, out var decisions, out var decidedBy, out var argumentError))
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

        // Deliberately *not* an unrestricted context, for the same reason
        // `migrate-nayax-connection` is not (AGENTS.md § Tenant ownership and data isolation). The
        // Businesses table carries no tenant query filter, so the enumeration below reads it under
        // a denied scope; every sale, product and audit row is then read and written through a
        // context scoped to the one business it belongs to, with the central query filters and
        // BusinessOwnershipEnforcer fully in force.
        await using var tenancyDb = new AppDbContext(dbOptions, new BusinessScope());
        var businessIds = await tenancyDb.Businesses
            .AsNoTracking()
            .OrderBy(business => business.Id)
            .Select(business => business.Id)
            .ToListAsync(cancellationToken);

        var reports = new List<(int BusinessId, NayaxSaleProductResolutionReport Report)>();
        var appliedSaleIds = new HashSet<long>();

        foreach (var businessId in businessIds)
        {
            await using var scopedDb = new AppDbContext(dbOptions, ResolvedScopeFor(businessId));
            var useCase = new ResolveNayaxSaleProducts(
                new EfNayaxSaleProductResolutionStore(scopedDb, clock));

            var report = await useCase.Handle(decisions, decidedBy, dryRun: !apply, cancellationToken);
            foreach (var applied in report.Applied)
                appliedSaleIds.Add(applied.SaleId);

            if (report.Unresolved.Count > 0 || report.Applied.Count > 0 || report.Refusals.Count > 0)
                reports.Add((businessId, report));
        }

        // A decision no business claimed names a sale that is not unresolved anywhere: already
        // decided, or simply not a sale. Reported once, not once per business.
        var unclaimed = decisions
            .Where(decision => !appliedSaleIds.Contains(decision.SaleId))
            .Select(decision => decision.SaleId)
            .ToList();

        Write(Console.Out, reports, unclaimed, apply);

        return reports.Any(entry => entry.Report.Refusals.Count > 0) || unclaimed.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// The scope a request for this business would carry. The business id comes from the database
    /// this command read it in, never from the command line.
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
        output.WriteLine($"Nayax sale product resolution - REFUSED: {reason}");
        output.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prints what an operator reviews before authorising an apply, and again afterwards. Nothing
    /// printed here is a secret: sale keys, transaction and machine identifiers, product keys and
    /// the reported product name are the business's own operational data, and no credential, token
    /// or amount of money is shown.
    /// </summary>
    public static void Write(
        TextWriter output,
        IReadOnlyList<(int BusinessId, NayaxSaleProductResolutionReport Report)> reports,
        IReadOnlyList<long> unclaimedDecisions,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(unclaimedDecisions);

        output.WriteLine();
        output.WriteLine(
            $"Nayax sale product resolution - {(apply ? "APPLY" : "DRY RUN (nothing will be written)")}");
        output.WriteLine(new string('-', 78));

        if (reports.Count == 0)
        {
            output.WriteLine("No business has an unresolved sale. Nothing to decide.");
            output.WriteLine();
            return;
        }

        foreach (var (businessId, report) in reports)
        {
            output.WriteLine();
            output.WriteLine($"Business {businessId.ToString(CultureInfo.InvariantCulture)}");
            output.WriteLine(
                $"  Unresolved sales      : {report.Unresolved.Count.ToString(CultureInfo.InvariantCulture)}");
            output.WriteLine(
                $"  ... conflicting       : {report.ConflictingCount.ToString(CultureInfo.InvariantCulture)}");
            output.WriteLine(
                $"  ... costed today      : {report.UnresolvedCostedCount.ToString(CultureInfo.InvariantCulture)}"
                    + "   <- the release refuses to start while this is above zero");

            foreach (var sale in report.Unresolved)
            {
                var candidates = sale.ConflictDirectCandidateProductId is { } direct
                    ? $"conflicting, candidates {direct.ToString(CultureInfo.InvariantCulture)} and "
                        + (sale.ConflictExternalCandidateProductId ?? 0).ToString(CultureInfo.InvariantCulture)
                    : "no recorded candidate";
                output.WriteLine(
                    $"    sale {sale.SaleId.ToString(CultureInfo.InvariantCulture)}"
                        + $" transaction {sale.TransactionId.ToString(CultureInfo.InvariantCulture)}"
                        + $" machine {sale.MachineId.ToString(CultureInfo.InvariantCulture)}"
                        + $" at {sale.AuthorizationTimeUtc.ToString("u", CultureInfo.InvariantCulture)}"
                        + $" name '{sale.ProductName ?? "(none)"}'"
                        + $" provider id {Describe(sale.ProviderProductId)}"
                        + $" costed {(sale.IsCosted ? "yes" : "no")}"
                        + $" - {candidates}");
            }

            foreach (var applied in report.Applied)
                output.WriteLine(
                    $"    {(report.DryRun ? "would resolve" : "resolved")}"
                        + $" sale {applied.SaleId.ToString(CultureInfo.InvariantCulture)}"
                        + $" (transaction {applied.TransactionId.ToString(CultureInfo.InvariantCulture)})"
                        + $" to product {applied.ProductId.ToString(CultureInfo.InvariantCulture)}");

            foreach (var refusal in report.Refusals)
                output.WriteLine($"    REFUSED: {refusal}");
        }

        foreach (var saleId in unclaimedDecisions)
            output.WriteLine(
                $"  REFUSED: sale {saleId.ToString(CultureInfo.InvariantCulture)} is not unresolved in "
                    + "any business - it may already have been decided. Nothing was written for it.");

        output.WriteLine();
    }

    private static string Describe(long? value) =>
        value is null ? "(unknown provenance)" : value.Value.ToString(CultureInfo.InvariantCulture);
}
