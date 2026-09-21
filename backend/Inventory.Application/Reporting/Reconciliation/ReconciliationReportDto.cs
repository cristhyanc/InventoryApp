using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Reconciliation;

public record ReconciliationReportDto(
    DateTime From,
    DateTime To,
    decimal NayaxSales,
    decimal ImportedReimbursement,
    decimal Difference,
    decimal Tolerance,
    bool IsMatch,
    ReportingDataQualityDto DataQuality,
    int CardTransactionCount = 0,
    int NayaxTransactionCount = 0,
    int CountDifference = 0,
    decimal ProcessingFees = 0m,
    decimal NetReimbursement = 0m,
    DateTime? PayoutDate = null)
{
    public decimal TotalVendingSales { get; init; } = NayaxSales;
    public decimal CardSales { get; init; }
    public decimal CashSales { get; init; }
    public int TotalTransactionCount { get; init; }
    public int CashTransactionCount { get; init; }
    public decimal CardTransactionSales { get; init; } = NayaxSales;
    public decimal NayaxReportedGrossCardSales { get; init; } = ImportedReimbursement;
    public int NayaxReportedCardTransactionCount { get; init; } = NayaxTransactionCount;
    public decimal GrossDifference { get; init; } = Difference;
    public string GrossStatus { get; init; } = IsMatch ? "Reconciled" : "Mismatch";
    public decimal ProcessingFeesExGst { get; init; } = ProcessingFees;
    public decimal FeeGst { get; init; }
    public decimal OtherFees { get; init; }
    public decimal Adjustments { get; init; }
    public bool AdjustmentsSupported { get; init; }
    public decimal ExpectedNetReimbursement { get; init; }
    public decimal ActualNetReimbursement { get; init; } = NetReimbursement;
    public decimal SettlementDifference { get; init; }
    public string SettlementStatus { get; init; } = "Pending";
    public string Status { get; init; } = IsMatch ? "Reconciled" : "Mismatch";
    public int PendingTransactionCount { get; init; }
    public int RefundedTransactionCount { get; init; }
    public int DeclinedOrCancelledTransactionCount { get; init; }
    public int UnknownStatusTransactionCount { get; init; }
    public IReadOnlyList<ReconciliationPeriodDto> PeriodRows { get; init; } = Array.Empty<ReconciliationPeriodDto>();
    public ReconciliationTotalsDto? Totals { get; init; }
}
