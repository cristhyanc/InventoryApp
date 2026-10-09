namespace Inventory.Application.Reporting.Reconciliation;

/// <summary>
/// Already-aggregated raw facts for one reconciliation period: either a matched imported
/// reimbursement period, or the single fallback period used when no reimbursement matches the
/// requested range. Contains no reconciliation-status/tolerance formulas: those live in
/// <c>Inventory.Domain.Reporting.Reconciliation.ReconciliationPeriodPolicy</c>.
///
/// The five non-completed transaction counts are this period's own (issue #477), counted inside its
/// coverage dates and machine scope before the completed-sale filter, so a period-level
/// data-quality flag or note describes that period. They are never the report's aggregate counts
/// repeated, and they never change which transactions the period's amounts include.
/// </summary>
public sealed record ReconciliationPeriodFacts(
    DateTime From,
    DateTime To,
    DateTime? PayoutDate,
    bool HasImported,
    decimal TotalVendingSales,
    decimal CardSales,
    decimal CashSales,
    int CardTransactionCount,
    int TotalTransactionCount,
    int CashTransactionCount,
    decimal ReportedGross,
    int ReportedCount,
    decimal ProcessingFeesExGst,
    decimal FeeGst,
    decimal OtherFees,
    bool HasGstClassification,
    bool Warning,
    bool PaymentDetailMissing,
    decimal ActualNetReimbursement,
    int PendingTransactionCount,
    int RefundedTransactionCount,
    int DeclinedOrCancelledTransactionCount,
    int UnknownStatusTransactionCount,
    int MissingStatusTransactionCount);

/// <summary>
/// Raw facts needed to build the reconciliation report for a resolved date range and optional
/// machine filter: one row per matched reimbursement period (or a single fallback period when none
/// matched), plus the period's own completed/all-status sales totals used for the report's quality
/// notes.
/// </summary>
public sealed record ReconciliationReportFacts(
    IReadOnlyList<ReconciliationPeriodFacts> Periods,
    bool HasMatchedReimbursement,
    decimal TotalVendingSales,
    decimal CardSales,
    decimal CashSales,
    int TotalTransactionCount,
    int CardTransactionCount,
    int CashTransactionCount,
    int UnknownPaymentTransactionCount,
    int PendingTransactionCount,
    int RefundedTransactionCount,
    int DeclinedOrCancelledTransactionCount,
    int UnknownStatusTransactionCount,
    int NullStatusTransactionCount,
    bool IsMachineFiltered);
