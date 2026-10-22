namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// Raw, already-aggregated facts unique to the dashboard summary for a resolved date range and
/// optional machine filter: the distinct machine/product counts over the period's completed sales,
/// and the commission completeness facts its data-quality notes need. Every other dashboard figure
/// (sales, profit, fees, commission amount, operating expenses, card/cash split) is already
/// available from the bookkeeping and product profitability reports this feature's use case
/// composes, so it is not duplicated here.
///
/// <para>The completed-transaction count was here until issue #804 moved it into
/// <c>BookkeepingReportFacts</c>, where the report that already counts those same rows owns it, and
/// the imported reimbursement facts (<c>ImportedContainsRows</c>/<c>ImportedNetSettlement</c>)
/// followed with the reconciliation comparison itself in issue #805 - they came from the same
/// shared imported-settlement query the bookkeeping adapter already reads. This summary composes
/// the bookkeeping report for all of them, so the two pages cannot report a different transaction
/// count, average sale, reimbursement or reconciliation status for one period.</para>
/// </summary>
public sealed record DashboardReportFacts(
    int MachineCount,
    int ProductCount,
    bool CommissionIsComplete,
    IReadOnlyList<string> CommissionWarnings);
