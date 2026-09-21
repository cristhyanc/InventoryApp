namespace Inventory.Application.Reporting.Daily;

public record DailyReportRowDto(
    DateTime Date,
    decimal Sales,
    decimal Quantity,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    int TransactionCount,
    decimal GrossSales = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m,
    decimal AverageSale = 0m,
    bool IsCogsComplete = true,
    int UncostedTransactionCount = 0,
    decimal UncostedSalesAmount = 0m,
    decimal? GrossMarginPercent = null,
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
    string NayaxFeeSource = "None")
{
    public decimal PartialCostOfGoods { get; init; }
}
