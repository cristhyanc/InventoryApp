using Inventory.Application.Dashboard;
using Inventory.Application.Machines;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IDashboardSummarySalesFactsProvider"/> (issue #459). It
/// lives beside the other <c>Ef&lt;Feature&gt;</c> adapters because it depends on
/// <see cref="AppDbContext"/> and the <c>NayaxSales</c> persistence model (issues #307/#309).
///
/// It selects completed sales through <see cref="EfNayaxSalesQueries.CompletedSalePredicate"/>, the
/// one translated "status 12 is an approved sale" filter every other sales query in this assembly
/// uses, so this card's revenue cannot drift from the reports'. The <see cref="AppDbContext"/> global
/// business query filter scopes every read here to the caller's business; there is deliberately no
/// per-call tenant predicate (AGENTS.md § Tenant ownership and data isolation).
///
/// Both period bounds are compared inclusively against the persisted UTC
/// <c>MachineAuthorizationTime</c>, the same convention <see cref="EfMachineDashboardFactsStore"/>
/// applies to the periods this one is handed, and the instants themselves were resolved from the
/// <c>Australia/Sydney</c> business day by the use case.
/// </summary>
public sealed class EfDashboardSummarySalesFactsProvider : IDashboardSummarySalesFactsProvider
{
    private readonly AppDbContext _db;

    public EfDashboardSummarySalesFactsProvider(AppDbContext db) => _db = db;

    public async Task<DashboardSummarySalesFacts> GetSalesFactsAsync(
        MachineDashboardPeriodUtc currentPeriod,
        MachineDashboardPeriodUtc priorPeriod,
        CancellationToken cancellationToken)
    {
        var completedSales = _db.NayaxSales.AsNoTracking().Where(EfNayaxSalesQueries.CompletedSalePredicate);

        // One grouped read covers both periods. A sale outside both is excluded by the predicate
        // before grouping, so this never loads the business's whole sales history to total two weeks.
        // The two periods cannot overlap - MachineDashboardWindow holds the comparable period's end
        // at the current week's own start - so grouping on "is in the current period" partitions the
        // selected sales exactly, and no sale can be counted in both totals.
        var periodTotals = await completedSales
            .Where(sale => (sale.MachineAuthorizationTime >= currentPeriod.StartUtc &&
                            sale.MachineAuthorizationTime <= currentPeriod.EndUtc) ||
                           (sale.MachineAuthorizationTime >= priorPeriod.StartUtc &&
                            sale.MachineAuthorizationTime <= priorPeriod.EndUtc))
            .GroupBy(sale => sale.MachineAuthorizationTime >= currentPeriod.StartUtc &&
                             sale.MachineAuthorizationTime <= currentPeriod.EndUtc)
            .Select(period => new
            {
                IsCurrentPeriod = period.Key,
                Sales = period.Sum(sale => sale.SettlementValue),
                TransactionCount = period.Count(),
            })
            .ToListAsync(cancellationToken);

        // The earliest recorded completed sale, which is how the Domain comparison rule tells a prior
        // period with no trade from one the recorded data never reached back to.
        var earliestRecordedSale = await completedSales
            .MinAsync(sale => (DateTime?)sale.MachineAuthorizationTime, cancellationToken);

        var current = periodTotals.FirstOrDefault(period => period.IsCurrentPeriod);
        var prior = periodTotals.FirstOrDefault(period => !period.IsCurrentPeriod);

        return new DashboardSummarySalesFacts(
            current?.Sales ?? 0m,
            current?.TransactionCount ?? 0,
            prior?.Sales ?? 0m,
            prior?.TransactionCount ?? 0,
            earliestRecordedSale);
    }
}
