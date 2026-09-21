using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Bookkeeping;

/// <summary>
/// Raw, already-aggregated facts needed to build the bookkeeping report for a resolved date range
/// and optional machine filter. Contains no financial formulas: those live in
/// <c>Inventory.Domain.Reporting.Bookkeeping</c> and in this feature's use case.
/// </summary>
public sealed record BookkeepingReportFacts(
    decimal GrossSales,
    decimal CardSales,
    int CardTransactions,
    decimal CashSales,
    int CashTransactions,
    int UnknownTransactions,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    decimal ReceiptDeliveryCost,
    decimal ReceiptPackageCost,
    bool ImportedHasNetSettlement,
    decimal ImportedNetSettlement,
    bool ImportedContainsRows,
    bool ImportedContainsGstClassification,
    bool ImportedMachineFilterMatched,
    bool ImportedFeesMachineFilterLimited,
    decimal OperatingExpensesTotal,
    decimal OperatingExpensesGst,
    IReadOnlyDictionary<string, decimal> OperatingExpensesByCategory,
    decimal SiteCommission,
    // Scoped to the selected machine when a machine filter is active; otherwise whole-business.
    // Used only to gate direct/net profit completeness.
    bool CommissionCompleteForScope,
    // The commission service's own overall completeness flag, independent of CommissionCompleteForScope.
    // Used only for the human-readable "commission configuration is incomplete" data-quality note.
    bool CommissionIsComplete,
    IReadOnlyList<string> CommissionWarnings,
    NayaxProcessingFeeResult ProcessingFees);
