using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Daily;

public record DailyReportTotalsDto(
    decimal GrossSales,
    decimal CardSales,
    decimal CashSales,
    decimal Quantity,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    int TransactionCount,
    decimal AverageSale,
    bool IsCogsComplete,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    decimal? GrossMarginPercent,
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
    public decimal PartialCostOfGoods { get; init; }
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}
