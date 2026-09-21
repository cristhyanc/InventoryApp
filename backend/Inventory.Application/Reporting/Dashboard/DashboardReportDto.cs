using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Dashboard;

public record DashboardReportDto(
    DateTime From,
    DateTime To,
    decimal Sales,
    decimal? GrossProfit,
    int Transactions,
    decimal Quantity,
    int MachineCount,
    int ProductCount,
    int UnmappedProductCount,
    ReportingDataQualityDto DataQuality,
    decimal NayaxFees = 0m,
    decimal NetReimbursement = 0m,
    decimal SiteCommission = 0m,
    decimal? NetProfit = null,
    decimal? NetMarginPercent = null,
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
    public decimal? CostOfGoodsSold { get; init; }
    public decimal PartialCostOfGoods { get; init; }
    public bool IsCogsComplete { get; init; } = true;
    public int UncostedTransactionCount { get; init; }
    public decimal UncostedSalesAmount { get; init; }
    public decimal AverageSale { get; init; } = Transactions == 0 ? 0m : Sales / Transactions;
    public decimal? GrossMarginPercent { get; init; } = GrossProfit.HasValue && Sales != 0m ? GrossProfit / Sales * 100m : null;
    public decimal NayaxFeesIncludingGst { get; init; } = 0m;
    public decimal ExpectedReimbursement { get; init; } = 0m;
    public decimal ActualReimbursement { get; init; } = NetReimbursement;
    public decimal ReimbursementDifference { get; init; } = 0m;
    public bool IsReconciled { get; init; }
    public string ReconciliationStatus { get; init; } = "Pending";
    public decimal ReconciliationTolerance { get; init; } = 0.01m;
    public bool AdjustmentsSupported { get; init; }
    public decimal? DirectProfit { get; init; }
    public decimal? DirectMarginPercent { get; init; }
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}
