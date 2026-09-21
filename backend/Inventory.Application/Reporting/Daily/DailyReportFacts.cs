using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Daily;

/// <summary>
/// Already-aggregated raw facts for one calendar day within the requested daily-report range.
/// Contains no financial formulas: those live in <c>Inventory.Domain.Reporting.Daily</c> and in
/// this feature's use case.
/// </summary>
public sealed record DailyReportDayFacts(
    DateTime Date,
    decimal GrossSales,
    int TransactionCount,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    decimal CardSales,
    decimal CashSales,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    NayaxProcessingFeeResult ProcessingFees,
    decimal ImportedReimbursement,
    decimal NetReimbursement,
    bool HasImportedReimbursement,
    bool HasPeriodOnlyImportedData,
    bool HasDataQualityWarning,
    int CompletedTransactionCount,
    int PendingTransactionCount,
    int DeclinedOrCancelledTransactionCount,
    int RefundedTransactionCount,
    int UnknownStatusTransactionCount);

/// <summary>
/// Already-aggregated raw facts for the whole requested range, used to build the totals row. Some
/// values (COGS completeness, processing fees, imported settlement, status counts) are computed
/// once over the whole period rather than summed from days, matching the legacy implementation's
/// separate period-level queries.
/// </summary>
public sealed record DailyReportTotalsFacts(
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    NayaxProcessingFeeResult ProcessingFees,
    bool ImportedContainsRows,
    decimal ImportedReimbursement,
    decimal ImportedNetReimbursement,
    int CompletedTransactionCount,
    int PendingTransactionCount,
    int DeclinedOrCancelledTransactionCount,
    int RefundedTransactionCount,
    int UnknownStatusTransactionCount,
    int NullStatusTransactionCount);

/// <summary>
/// Raw facts needed to build the daily report for a resolved date range and optional machine
/// filter.
/// </summary>
public sealed record DailyReportFacts(
    IReadOnlyList<DailyReportDayFacts> Days,
    DailyReportTotalsFacts Totals,
    bool ImportedContainsRows,
    bool ImportedContainsGstClassification,
    bool ImportedFeesMachineFilterLimited,
    bool HasAnyPeriodOnlyImportedData);
