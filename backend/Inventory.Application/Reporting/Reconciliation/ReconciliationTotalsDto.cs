namespace Inventory.Application.Reporting.Reconciliation;

public record ReconciliationTotalsDto(
    decimal TotalVendingSales,
    decimal CardSales,
    decimal CashSales,
    decimal CardTransactionSales,
    decimal NayaxReportedGrossCardSales,
    int CardTransactionCount,
    int NayaxReportedCardTransactionCount,
    int CountDifference,
    decimal GrossDifference,
    decimal ProcessingFeesExGst,
    decimal FeeGst,
    decimal OtherFees,
    decimal Adjustments,
    decimal ExpectedNetReimbursement,
    decimal ActualNetReimbursement,
    decimal SettlementDifference,
    string GrossStatus,
    string SettlementStatus,
    string Status)
{
    public int TotalTransactionCount { get; init; }
    public int CashTransactionCount { get; init; }
    public bool AdjustmentsSupported { get; init; }
}
