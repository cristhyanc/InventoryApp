using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Reconciliation;

public record ReconciliationPeriodDto(
    DateTime From,
    DateTime To,
    decimal TotalVendingSales,
    decimal CardSales,
    decimal CashSales,
    decimal CardTransactionSales,
    decimal NayaxReportedGrossCardSales,
    int CardTransactionCount,
    int NayaxReportedCardTransactionCount,
    int CountDifference,
    decimal GrossDifference,
    string GrossStatus,
    decimal ProcessingFeesExGst,
    decimal FeeGst,
    decimal OtherFees,
    decimal Adjustments,
    decimal ExpectedNetReimbursement,
    decimal ActualNetReimbursement,
    decimal SettlementDifference,
    string SettlementStatus,
    string Status,
    DateTime? PayoutDate,
    ReportingDataQualityDto DataQuality)
{
    public int TotalTransactionCount { get; init; }
    public int CashTransactionCount { get; init; }
    public bool AdjustmentsSupported { get; init; }
    public int PendingTransactionCount { get; init; }
    public int RefundedTransactionCount { get; init; }
    public int DeclinedOrCancelledTransactionCount { get; init; }
    public int UnknownStatusTransactionCount { get; init; }
}
