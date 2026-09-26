namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// The authoritative Dashboard "Inventory Value" tile contract: the business-owned perpetual
/// inventory value (<c>Product.InventoryValue</c>, the AVCO valuation), never the retail
/// <c>UnitPrice</c>. <see cref="TotalInventoryValue"/> is <c>null</c> whenever
/// <see cref="IsComplete"/> is <c>false</c> - a missing per-product cost must never be presented as
/// a real <c>$0.00</c> total. Angular displays this value/status as returned and performs no
/// valuation calculation of its own.
/// </summary>
public sealed record InventoryValuationSummaryDto(
    decimal? TotalInventoryValue,
    bool IsComplete,
    int ProductsWithUnknownCost,
    int TotalProducts);
