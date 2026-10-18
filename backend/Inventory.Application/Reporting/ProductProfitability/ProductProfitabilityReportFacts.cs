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
/// One group of already-aggregated sales facts. Since issue #758 the group's identity is the local
/// product the sales are attributed to; a group with no product holds sales nothing could resolve
/// and is reported as unresolved, keyed by the identity the provider reported so two different
/// unresolved identities stay apart.
///
/// <para><c>ProductId</c> is the local product, or <c>null</c> for an unresolved group.
/// <c>NayaxProductId</c> and <c>ProductName</c> carry the provider's reported identity for an
/// unresolved group only, and are <c>null</c> for a resolved one, whose identity is the product and
/// whose display name comes from the catalogue.</para>
/// </summary>
public sealed record ProductProfitabilitySaleGroupFacts(
    long? ProductId,
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
