namespace Inventory.Application.Reporting.Transactions;

public record TransactionSalesTotalsDto(
    int TransactionCount, int CompletedTransactionCount, decimal Sales, decimal CardSales, decimal CashSales,
    int CostedCompletedTransactionCount, int UncostedCompletedTransactionCount, bool IsCogsComplete,
    decimal? CostOfGoods, decimal PartialCostOfGoods, decimal? GrossProfit, decimal? GrossMarginPercent,
    decimal? DirectProfit, decimal? DirectMarginPercent, decimal? PartialGrossProfit, decimal? PartialDirectProfit,
    decimal EstimatedFeeExGst, decimal EstimatedFeeGst, decimal EstimatedFeeIncGst, decimal CommissionAmount);
