namespace Inventory.Application.Reporting.ProductProfitability;

public record ProductProfitabilityRowDto(
    long? ProductId,
    string ProductName,
    string? CategoryName,
    decimal Sales,
    decimal Quantity,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    decimal? MarginPercent,
    int TransactionCount,
    bool IsUnmapped,
    bool HistoricalCostAvailable,
    decimal CardRevenue = 0m,
    decimal CashRevenue = 0m)
{
    public decimal PartialCostOfGoods { get; init; }
    public bool IsCogsComplete { get; init; } = true;
    public int UncostedTransactionCount { get; init; }
    public decimal UncostedSalesAmount { get; init; }
}
