using Inventory.Application.Machines;

namespace Inventory.Application.Dashboard;

/// <summary>
/// The completed-sale facts the home Dashboard's sales card needs, for both of its periods at once.
/// <see cref="EarliestRecordedSaleUtc"/> is the instant of the business's earliest recorded completed
/// sale, or <c>null</c> when it has none; it is what lets
/// <see cref="Inventory.Domain.Reporting.Dashboard.PeriodRevenueComparisonPolicy"/> tell a prior
/// period with genuinely no trade from one the recorded data never reached.
///
/// The revenue figures are gross vending sales - the sum of <c>SettlementValue</c> over approved
/// sales only - which is the same definition the bookkeeping, daily and reporting-dashboard reports
/// use. They are not a Nayax payout, and card and cash sales are not separated here because this
/// card reports vending revenue, not settlement.
/// </summary>
public sealed record DashboardSummarySalesFacts(
    decimal CurrentPeriodSales,
    int CurrentPeriodTransactionCount,
    decimal PriorPeriodSales,
    int PriorPeriodTransactionCount,
    DateTime? EarliestRecordedSaleUtc);

/// <summary>
/// Narrow Application-owned port for those facts. Not a generic repository: it answers exactly one
/// question for two already-resolved periods, in one read, so the card never fans out a query per
/// period or per machine.
///
/// Both periods arrive as <see cref="MachineDashboardPeriodUtc"/> - the period type the site and
/// machine dashboards already resolve from the <c>Australia/Sydney</c> business day - so this port
/// never decides which week "this week" is, and its UTC instants are directly comparable with the
/// persisted UTC <c>MachineAuthorizationTime</c> of a sale.
/// </summary>
public interface IDashboardSummarySalesFactsProvider
{
    Task<DashboardSummarySalesFacts> GetSalesFactsAsync(
        MachineDashboardPeriodUtc currentPeriod,
        MachineDashboardPeriodUtc priorPeriod,
        CancellationToken cancellationToken);
}
