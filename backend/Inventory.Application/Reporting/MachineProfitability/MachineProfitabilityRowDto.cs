using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.MachineProfitability;

public record MachineProfitabilityRowDto(
    long MachineId,
    string MachineName,
    decimal Sales,
    decimal Quantity,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    decimal? MarginPercent,
    int TransactionCount,
    decimal SiteCommission = 0m,
    decimal? DirectProfit = null,
    decimal? DirectMarginPercent = null,
    decimal CommissionPercent = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m)
{
    public decimal PartialCostOfGoods { get; init; }
    public bool IsCogsComplete { get; init; } = true;
    public int UncostedTransactionCount { get; init; }
    public decimal UncostedSalesAmount { get; init; }
    public decimal DirectOperatingExpenses { get; init; }
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);
}
