using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.ProductProfitability;

/// <summary>
/// Raw, already-aggregated facts needed to build the product profitability report for a resolved
/// date range and optional machine filter. Contains no financial formulas or product-matching
/// logic: those live in <c>Inventory.Domain.Reporting.Profitability</c>/<c>ProductMatching</c> and
/// in this feature's use case.
/// </summary>
public sealed record ProductProfitabilityReportFacts(
    IReadOnlyList<ProductProfitabilitySaleGroupFacts> SaleGroups,
    IReadOnlyList<ProductProfitabilityCatalogueEntry> Catalogue)
{
    /// <summary>
    /// How well recorded provider coverage accounts for this period's stored sales (issue #731,
    /// extending issue #730's model to this report). Only the sales family is relevant: every figure
    /// in this report - sales, quantity, cost of goods, gross profit and the margin - is derived
    /// from stored provider sales, and no settlement import feeds any of them.
    /// </summary>
    public ReportCoverageFacts Coverage { get; init; } = new();
}

/// <summary>
/// One raw Nayax product identifier/name group's already-aggregated sales facts, before matching
/// against the product catalogue. Multiple sale groups can resolve to the same catalogue product
/// (or the same unmapped identity) and must then be merged by the use case.
/// </summary>
public sealed record ProductProfitabilitySaleGroupFacts(
    long? NayaxProductId,
    string? ProductName,
    decimal Sales,
    decimal Quantity,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    int TransactionCount,
    decimal CardRevenue,
    decimal CashRevenue);

/// <summary>A catalogue product candidate available for matching, with its display fields.</summary>
public sealed record ProductProfitabilityCatalogueEntry(long Id, string Name, string? CategoryName);
