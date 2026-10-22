namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// Raw, already-aggregated facts unique to the dashboard summary for a resolved date range and
/// optional machine filter: the distinct machine/product counts over the period's completed sales,
/// and the imported reimbursement/commission facts needed for the expected-versus-actual
/// reconciliation status and its data-quality notes. Every other dashboard figure (sales, profit,
/// fees, commission amount, operating expenses, card/cash split) is already available from the
/// bookkeeping and product profitability reports this feature's use case composes, so it is not
/// duplicated here.
///
/// <para>The completed-transaction count was here until issue #804 moved it into
/// <c>BookkeepingReportFacts</c>, where the report that already counts those same rows owns it.
/// This summary composes the bookkeeping report for it, so the two pages cannot report a different
/// transaction count or average sale for one period.</para>
/// </summary>
public sealed record DashboardReportFacts(
    int MachineCount,
    int ProductCount,
    bool ImportedContainsRows,
    decimal ImportedNetSettlement,
    bool CommissionIsComplete,
    IReadOnlyList<string> CommissionWarnings);
