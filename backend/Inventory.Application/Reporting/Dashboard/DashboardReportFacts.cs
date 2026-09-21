namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// Raw, already-aggregated facts unique to the dashboard summary for a resolved date range and
/// optional machine filter: the completed-sale transaction/machine/product counts and the imported
/// reimbursement/commission facts needed for the expected-versus-actual reconciliation status and
/// its data-quality notes. Every other dashboard figure (sales, profit, fees, commission amount,
/// operating expenses, card/cash split) is already available from the bookkeeping and product
/// profitability reports this feature's use case composes, so it is not duplicated here.
/// </summary>
public sealed record DashboardReportFacts(
    int TransactionCount,
    int MachineCount,
    int ProductCount,
    bool ImportedContainsRows,
    decimal ImportedNetSettlement,
    bool CommissionIsComplete,
    IReadOnlyList<string> CommissionWarnings);
