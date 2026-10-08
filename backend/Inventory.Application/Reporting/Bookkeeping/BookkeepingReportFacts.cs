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
    NayaxProcessingFeeResult ProcessingFees,
    // Transaction-status diagnostics for the requested business, date range and machine scope,
    // counted before the completed-sale filter excludes them (issue #476), so the report can say
    // what was actually excluded and why. They never change which sales the financial totals
    // include: only status 12 is a completed sale, and every amount above is still derived from
    // completed sales alone.
    //
    // The kinds stay separate because they mean different things. Pending, refunded and
    // cancelled/declined rows are normal Nayax outcomes, not data errors.
    // UnknownStatusTransactionCount counts rows whose status ID is present but unrecognised, and
    // MissingStatusTransactionCount counts rows carrying no status ID at all; the two never
    // overlap, and only those two are data-quality problems.
    int PendingTransactionCount,
    int RefundedTransactionCount,
    int DeclinedOrCancelledTransactionCount,
    int UnknownStatusTransactionCount,
    int MissingStatusTransactionCount);
