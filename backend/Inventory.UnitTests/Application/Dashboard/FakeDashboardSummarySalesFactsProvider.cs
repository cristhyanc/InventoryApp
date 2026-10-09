using Inventory.Application.Dashboard;
using Inventory.Application.Machines;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// In-memory fake of the Dashboard summary sales facts port, so the use case's orchestration and the
/// Domain rules it applies are exercised without EF Core. It records the periods it was handed, which
/// is how the tests prove the use case passes the resolved Sydney week-to-date and its comparable
/// period rather than re-deriving a range of its own.
/// </summary>
public sealed class FakeDashboardSummarySalesFactsProvider : IDashboardSummarySalesFactsProvider
{
    private readonly DashboardSummarySalesFacts _facts;

    public FakeDashboardSummarySalesFactsProvider(DashboardSummarySalesFacts facts) => _facts = facts;

    public FakeDashboardSummarySalesFactsProvider(
        decimal currentPeriodSales = 0m,
        int currentPeriodTransactionCount = 0,
        decimal priorPeriodSales = 0m,
        int priorPeriodTransactionCount = 0,
        DateTime? earliestRecordedSaleUtc = null)
        : this(new DashboardSummarySalesFacts(
            currentPeriodSales,
            currentPeriodTransactionCount,
            priorPeriodSales,
            priorPeriodTransactionCount,
            earliestRecordedSaleUtc))
    {
    }

    public int Calls { get; private set; }

    public MachineDashboardPeriodUtc? CurrentPeriod { get; private set; }

    public MachineDashboardPeriodUtc? PriorPeriod { get; private set; }

    public Task<DashboardSummarySalesFacts> GetSalesFactsAsync(
        MachineDashboardPeriodUtc currentPeriod,
        MachineDashboardPeriodUtc priorPeriod,
        CancellationToken cancellationToken)
    {
        Calls++;
        CurrentPeriod = currentPeriod;
        PriorPeriod = priorPeriod;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_facts);
    }
}
