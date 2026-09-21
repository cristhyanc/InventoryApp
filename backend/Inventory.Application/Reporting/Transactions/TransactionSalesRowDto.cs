namespace Inventory.Application.Reporting.Transactions;

public record TransactionSalesRowDto(
    DateTime TransactionDate, long TransactionId, long MachineId, string MachineName,
    long? SiteId, string? SiteName, long? ProductId, string ProductName,
    string PaymentType, string? RawPaymentMethod, decimal Sale,
    decimal? NayaxProductCostPrice, decimal? UnitCostAtSale, decimal? CostOfGoods,
    string CostingStatus, string CostSource,
    decimal? GrossProfit, decimal? GrossMarginPercent, decimal? DirectProfit, decimal? DirectMarginPercent,
    decimal? FeeExGst, decimal? FeeGst, decimal? FeeIncGst, string FeeSource,
    decimal? CommissionRate, string? CommissionBasis, decimal? CommissionAmount,
    int? TransactionStatusId, string TransactionStatus, bool IsCompleted);
