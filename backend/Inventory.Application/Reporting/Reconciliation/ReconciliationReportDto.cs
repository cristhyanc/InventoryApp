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

    /// <summary>
    /// Transactions in the requested scope carrying no status ID at all (issue #477). Kept distinct
    /// from <see cref="UnknownStatusTransactionCount"/>, which counts a status ID that is present but
    /// not one this application recognises. Both are data-quality problems; neither changes which
    /// transactions the reconciliation figures include.
    /// </summary>
    public int MissingStatusTransactionCount { get; init; }
    public IReadOnlyList<ReconciliationPeriodDto> PeriodRows { get; init; } = Array.Empty<ReconciliationPeriodDto>();
    public ReconciliationTotalsDto? Totals { get; init; }

    /// <summary>
    /// How well recorded provider coverage accounts for this period's provider-sourced figures
    /// (issue #731, under issue #730's model). <c>Sales</c> governs the recorded vending, card and
    /// cash sales and their transaction counts; <c>Fees</c> governs the Nayax-reported gross card
    /// sales and count, the processing fees and their GST, the other fees and the actual net
    /// reimbursement; <c>Combined</c> governs the differences, the expected net reimbursement and
    /// the reconciliation statuses, which compare the two sides. Only <c>Complete</c> permits a zero
    /// in the figures it governs to be presented as a real zero - and a difference of <c>0.00</c>
    /// between two sides that were never both measured is the most misleading zero in this report.
    /// </summary>
    public ReportProviderAvailabilityDto ProviderAvailability { get; init; } = ReportProviderAvailabilityDto.Undecided;
}
