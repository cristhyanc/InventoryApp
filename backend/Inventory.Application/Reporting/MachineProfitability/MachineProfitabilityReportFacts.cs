using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.MachineProfitability;

/// <summary>
/// Raw, already-aggregated facts needed to build the machine profitability report for a resolved
/// date range and optional machine filter. Contains no financial formulas: those live in
/// <c>Inventory.Domain.Reporting.Profitability</c> and in this feature's use case.
/// </summary>
public sealed record MachineProfitabilityReportFacts(
    IReadOnlyList<MachineProfitabilityMachineFacts> Machines,
    int MissingFeeRateTransactionCount,
    bool CommissionIsComplete,
    IReadOnlyList<string> CommissionWarnings)
{
    /// <summary>
    /// How well recorded provider coverage accounts for this period (issue #731, extending issue
    /// #730's model to this report). Both families matter: each machine's sales, quantity, cost of
    /// goods, gross profit and commission come from stored provider sales, while the actual part of
    /// its Nayax processing fees comes from committed settlement imports.
    /// </summary>
    public ReportCoverageFacts Coverage { get; init; } = new();
}

/// <summary>One machine's already-aggregated sales/cost/commission/fee/expense facts.</summary>
public sealed record MachineProfitabilityMachineFacts(
    long MachineId,
    string MachineName,
    decimal Sales,
    decimal CardSales,
    decimal CashSales,
    decimal Quantity,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    int TransactionCount,
    decimal CommissionPercent,
    decimal CommissionDue,
    bool CommissionComplete,
    decimal OperatingExpenses,
    NayaxProcessingFeeResult ProcessingFees);
