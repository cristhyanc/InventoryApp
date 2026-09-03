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
    decimal OtherOperatingExpenses = 0m);

public record DailyReportRowDto(
    DateTime Date,
    decimal Sales,
    decimal Quantity,
    decimal CostOfGoods,
    decimal GrossProfit,
    int TransactionCount);

public record DailyReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<DailyReportRowDto> Rows,
    ReportingDataQualityDto DataQuality);

public record ReconciliationReportDto(
    DateTime From,
    DateTime To,
    decimal NayaxSales,
    decimal ImportedReimbursement,
    decimal Difference,
    decimal Tolerance,
    bool IsMatch,
    ReportingDataQualityDto DataQuality);

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
    decimal CommissionPercent = 0m);

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
    bool HistoricalCostAvailable);

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
    decimal InventoryPurchaseGst = 0m);

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
    decimal OtherOperatingExpenses = 0m);
