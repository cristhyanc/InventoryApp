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

    /// <summary>
    /// Purchasing insight (issue #207), derived from the same authoritative #63
    /// <c>SupplierPriceComparisonPolicy</c> used by the product's own price-history view. Never fed
    /// into <see cref="CostOfGoods"/>/<see cref="GrossProfit"/>/<see cref="MarginPercent"/> above,
    /// which continue to come from historical completed-sale costing only. Null when the product has
    /// no recorded Purchase history.
    /// </summary>
    public decimal? LastCost { get; init; }
    public string? LastCostSupplierName { get; init; }
    public decimal? LowestCost { get; init; }
    public string? LowestCostSupplierName { get; init; }
    public decimal? SavingPerUnit { get; init; }
}
