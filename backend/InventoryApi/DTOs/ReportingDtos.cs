namespace InventoryApi.DTOs;

/// <summary>Common inclusive date and machine filters used by reporting endpoints.</summary>
public record ReportingFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    long? MachineId = null,
    string? FinancialYear = null)
{
    public DateTime? StartDate { get; init; } = From;
    public DateTime? EndDate { get; init; } = To;
    public long? MachineID { get; init; } = MachineId;
}

public record ReportFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    long? MachineId = null,
    string? FinancialYear = null) : ReportingFilterDto(From, To, MachineId, FinancialYear);

/// <summary>Filters the transaction sales report. The service restricts page sizes to 50, 100, or 250.</summary>
public record TransactionSalesFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    long? MachineId = null,
    long? SiteId = null,
    long? ProductId = null,
    string? PaymentType = null,
    string? Status = "completed",
    string? CogsStatus = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50,
    string? SortBy = "date",
    bool SortDescending = true);

public record TransactionSalesFilterOptionDto(long? Id, string Name);

public record TransactionSalesFilterOptionsDto(
    IReadOnlyList<TransactionSalesFilterOptionDto> Sites,
    IReadOnlyList<TransactionSalesFilterOptionDto> Products);

public record TransactionSalesRowDto(
    DateTime TransactionDate, long TransactionId, long MachineId, string MachineName,
    long? SiteId, string? SiteName, long? ProductId, string ProductName,
    string PaymentType, string? RawPaymentMethod, decimal Sale,
    decimal? UnitCostAtSale, decimal? CostOfGoods, string CostingStatus,
    decimal? GrossProfit, decimal? GrossMarginPercent, decimal? DirectProfit, decimal? DirectMarginPercent,
    decimal? FeeExGst, decimal? FeeGst, decimal? FeeIncGst, string FeeSource,
    decimal? CommissionRate, string? CommissionBasis, decimal? CommissionAmount,
    int? TransactionStatusId, string TransactionStatus, bool IsCompleted);

public record TransactionSalesTotalsDto(
    int TransactionCount, int CompletedTransactionCount, decimal Sales, decimal CardSales, decimal CashSales,
    int CostedCompletedTransactionCount, int UncostedCompletedTransactionCount, bool IsCogsComplete,
    decimal? CostOfGoods, decimal PartialCostOfGoods, decimal? GrossProfit, decimal? GrossMarginPercent,
    decimal? DirectProfit, decimal? DirectMarginPercent, decimal? PartialGrossProfit, decimal? PartialDirectProfit,
    decimal EstimatedFeeExGst, decimal EstimatedFeeGst, decimal EstimatedFeeIncGst, decimal CommissionAmount);

public record TransactionSalesReportDto(
    DateTime From, DateTime To, IReadOnlyList<TransactionSalesRowDto> Rows, TransactionSalesTotalsDto Totals,
    ReportingDataQualityDto DataQuality, int Page, int PageSize, int TotalCount,
    TransactionSalesFilterOptionsDto FilterOptions);

public record ReportingDataQualityDto(
    bool MissingStatus = true,
    bool HistoricalCostUnavailable = true,
    bool GstClassificationMissing = true,
    bool CommissionNotPersisted = true,
    bool ContainsUnmappedProducts = false,
    IReadOnlyList<string>? Notes = null)
{
    public bool MissingStatusData => MissingStatus;
    public bool HistoricalCostMissing => HistoricalCostUnavailable;
    public bool GstClassificationMissingData => GstClassificationMissing;
    public bool CommissionPersistenceLimited => CommissionNotPersisted;
}

public record NayaxProcessingFeeResult(
    decimal ActualFeeExGst,
    decimal ActualFeeGst,
    decimal ActualFeeIncGst,
    decimal EstimatedFeeExGst,
    decimal EstimatedFeeGst,
    decimal EstimatedFeeIncGst,
    int EstimatedCardTransactionCount,
    DateTime? ActualFeeCoverageEndDate,
    DateTime? EstimatedFeeFromDate)
{
    public decimal TotalFeeExGst => ActualFeeExGst + EstimatedFeeExGst;
    public decimal TotalFeeGst => ActualFeeGst + EstimatedFeeGst;
    public decimal TotalFeeIncGst => ActualFeeIncGst + EstimatedFeeIncGst;
    public bool HasEstimatedFees => EstimatedCardTransactionCount > 0;
    public bool IsFullyActual => EstimatedCardTransactionCount == 0;
}

public record BookkeepingReportDto(
    DateTime From,
    DateTime To,
    string FinancialYear,
    decimal Sales,
    decimal CostOfGoods,
    decimal GrossProfit,
    decimal Fees,
    decimal NetSettlement,
    decimal GstOnSales,
    decimal GstOnFees,
    ReportingDataQualityDto DataQuality,
    decimal SiteCommission = 0m,
    decimal NetProfit = 0m,
    decimal NetMarginPercent = 0m,
    decimal NayaxFeesExGst = 0m,
    decimal NayaxFeesIncludingGst = 0m,
    decimal DeliveryCosts = 0m,
    decimal PackageCosts = 0m,
    decimal OtherOperatingExpenses = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m,
    int CardTransactionCount = 0,
    int CashTransactionCount = 0,
    decimal NayaxProcessingRate = 0m,
    int PendingTransactionCount = 0,
    int RefundedTransactionCount = 0,
    int DeclinedOrCancelledTransactionCount = 0,
    int UnknownStatusTransactionCount = 0)
{
    public decimal StructuredOperatingExpenses { get; init; }
    public decimal OperatingExpenseGst { get; init; }
    public IReadOnlyDictionary<string, decimal> OperatingExpensesByCategory { get; init; } =
        new Dictionary<string, decimal>();
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}

public record DailyReportRowDto(
    DateTime Date,
    decimal Sales,
    decimal Quantity,
    decimal CostOfGoods,
    decimal GrossProfit,
    int TransactionCount,
    decimal GrossSales = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m,
    decimal AverageSale = 0m,
    bool IsCogsComplete = true,
    int UncostedTransactionCount = 0,
    decimal UncostedSalesAmount = 0m,
    decimal GrossMarginPercent = 0m,
    decimal NayaxFeesExGst = 0m,
    decimal NayaxFeesIncludingGst = 0m,
    decimal ImportedReimbursement = 0m,
    decimal NetReimbursement = 0m,
    bool IsReconciled = false,
    string ReconciliationStatus = "Unavailable",
    int CompletedTransactionCount = 0,
    int PendingTransactionCount = 0,
    int DeclinedOrCancelledTransactionCount = 0,
    int RefundedTransactionCount = 0,
    int UnknownStatusTransactionCount = 0,
    string NayaxFeeSource = "None");

public record DailyReportTotalsDto(
    decimal GrossSales,
    decimal CardSales,
    decimal CashSales,
    decimal Quantity,
    decimal CostOfGoods,
    decimal GrossProfit,
    int TransactionCount,
    decimal AverageSale,
    bool IsCogsComplete,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    decimal GrossMarginPercent,
    decimal NayaxFeesExGst,
    decimal NayaxFeesIncludingGst,
    decimal ImportedReimbursement,
    decimal NetReimbursement,
    int CompletedTransactionCount = 0,
    int PendingTransactionCount = 0,
    int DeclinedOrCancelledTransactionCount = 0,
    int RefundedTransactionCount = 0,
    int UnknownStatusTransactionCount = 0)
{
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}

public record DailyReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<DailyReportRowDto> Rows,
    ReportingDataQualityDto DataQuality,
    DailyReportTotalsDto? Totals = null);

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

public record MachineProfitabilityRowDto(
    long MachineId,
    string MachineName,
    decimal Sales,
    decimal Quantity,
    decimal CostOfGoods,
    decimal GrossProfit,
    decimal MarginPercent,
    int TransactionCount,
    decimal SiteCommission = 0m,
    decimal NetProfit = 0m,
    decimal NetMarginPercent = 0m,
    decimal CommissionPercent = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m)
{
    public decimal DirectOperatingExpenses { get; init; }
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}

public record MachineProfitabilityReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<MachineProfitabilityRowDto> Rows,
    ReportingDataQualityDto DataQuality);

public record ProductProfitabilityRowDto(
    long? ProductId,
    string ProductName,
    string? CategoryName,
    decimal Sales,
    decimal Quantity,
    decimal CostOfGoods,
    decimal GrossProfit,
    decimal MarginPercent,
    int TransactionCount,
    bool IsUnmapped,
    bool HistoricalCostAvailable,
    decimal CardRevenue = 0m,
    decimal CashRevenue = 0m);

public record ProductProfitabilityReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<ProductProfitabilityRowDto> Rows,
    ReportingDataQualityDto DataQuality);

public record GstAccountingAidDto(
    DateTime From,
    DateTime To,
    decimal TaxableSales,
    decimal GstOnSales,
    decimal TaxableFees,
    decimal GstOnFees,
    decimal NetGst,
    ReportingDataQualityDto DataQuality,
    decimal GstFreeSales = 0m,
    decimal InventoryPurchaseGst = 0m)
{
    public decimal OperatingExpenseGst { get; init; }
}

public record DashboardReportDto(
    DateTime From,
    DateTime To,
    decimal Sales,
    decimal GrossProfit,
    int Transactions,
    decimal Quantity,
    int MachineCount,
    int ProductCount,
    int UnmappedProductCount,
    ReportingDataQualityDto DataQuality,
    decimal NayaxFees = 0m,
    decimal NetReimbursement = 0m,
    decimal SiteCommission = 0m,
    decimal NetProfit = 0m,
    decimal NetMarginPercent = 0m,
    decimal NayaxFeesExGst = 0m,
    decimal DeliveryCosts = 0m,
    decimal PackageCosts = 0m,
    decimal OtherOperatingExpenses = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m,
    int CardTransactionCount = 0,
    int CashTransactionCount = 0)
{
    public decimal StructuredOperatingExpenses { get; init; }
    public decimal OperatingExpenseGst { get; init; }
    public decimal TotalSales { get; init; } = Sales;
    public decimal CostOfGoodsSold { get; init; } = 0m;
    public decimal AverageSale { get; init; } = Transactions == 0 ? 0m : Sales / Transactions;
    public decimal GrossMarginPercent { get; init; } = Sales == 0m ? 0m : GrossProfit / Sales * 100m;
    public decimal NayaxFeesIncludingGst { get; init; } = 0m;
    public decimal ExpectedReimbursement { get; init; } = 0m;
    public decimal ActualReimbursement { get; init; } = NetReimbursement;
    public decimal ReimbursementDifference { get; init; } = 0m;
    public bool IsReconciled { get; init; }
    public string ReconciliationStatus { get; init; } = "Pending";
    public decimal ReconciliationTolerance { get; init; } = 0.01m;
    public bool AdjustmentsSupported { get; init; }
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}
