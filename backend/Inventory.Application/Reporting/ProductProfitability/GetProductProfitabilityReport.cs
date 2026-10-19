using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Domain.Reporting.Profitability;

namespace Inventory.Application.Reporting.ProductProfitability;

/// <summary>
/// The product profitability report use case: resolves the requested date range/machine scope,
/// retrieves report facts through the narrow <see cref="IProductProfitabilityReportFactsProvider"/>
/// port, reads the local product each group of sales is persisted against (issue #758, parent #329
/// decision A2c) rather than matching one, merges groups
/// that resolve to the same product (or the same unresolved identity), applies the shared Domain
/// profit/margin/completeness policy, and builds the authoritative
/// <see cref="ProductProfitabilityReportDto"/> consumed by both the API response and the
/// CSV/XLSX export.
///
/// Each mapped product's Last/Lowest purchase-cost purchasing insight (issue #207) is derived by
/// fetching every matched product's actual Purchase history in one bulk call through
/// <see cref="IProductPurchaseCostFactsProvider"/> and applying the same authoritative #63
/// <see cref="SupplierPriceComparisonPolicy"/> the product's own price-history view uses - never a
/// second lowest/latest-cost algorithm, and never a per-product query.
/// </summary>
public sealed class GetProductProfitabilityReport : IGetProductProfitabilityReport
{
    private readonly IProductProfitabilityReportFactsProvider _facts;
    private readonly IProductPurchaseCostFactsProvider _purchaseCostFacts;

    public GetProductProfitabilityReport(
        IProductProfitabilityReportFactsProvider facts,
        IProductPurchaseCostFactsProvider purchaseCostFacts)
    {
        _facts = facts;
        _purchaseCostFacts = purchaseCostFacts;
    }

    public async Task<ProductProfitabilityReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var range = ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);
        var machineId = filter.MachineId ?? filter.MachineID;

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, machineId, cancellationToken);
        var catalogueById = facts.Catalogue.ToDictionary(x => x.Id);

        // The product each group of sales is attributed to is the association persisted on those
        // sales (issue #758), read here rather than matched again. A group with none is unresolved
        // and keeps the identity the provider reported, so it stays visible under a name an
        // operator can recognise instead of disappearing into another product's row.
        var matched = facts.SaleGroups.Select(group =>
        {
            var unmappedName = string.IsNullOrWhiteSpace(group.ProductName)
                ? "Unmapped product"
                : ProductMatcher.NormalizeName(group.ProductName);
            return (Group: group, MatchedProductId: group.ProductId, UnmappedName: unmappedName);
        }).ToList();

        var matchedProductIds = matched
            .Where(x => x.MatchedProductId.HasValue)
            .Select(x => x.MatchedProductId!.Value)
            .Distinct()
            .ToArray();
        var purchaseCostFactsByProduct = await _purchaseCostFacts.GetForProductsAsync(matchedProductIds, cancellationToken);

        var rows = matched
            .GroupBy(x => x.MatchedProductId.HasValue ? $"product:{x.MatchedProductId}" : $"unmapped:{x.Group.NayaxProductId}:{x.UnmappedName}")
            .Select(g =>
            {
                var first = g.First();
                var sales = g.Sum(x => x.Group.Sales);
                var partialCost = g.Sum(x => x.Group.PartialCostOfGoods);
                var isCogsComplete = g.All(x => x.Group.IsCogsComplete);
                var isUnmapped = first.MatchedProductId is null;
                var product = first.MatchedProductId.HasValue ? catalogueById.GetValueOrDefault(first.MatchedProductId.Value) : null;

                var row = ProfitabilityRowPolicy.Calculate(new ProfitabilityRowInputs(sales, partialCost, isCogsComplete));

                var purchaseCostComparison = first.MatchedProductId.HasValue
                    && purchaseCostFactsByProduct.TryGetValue(first.MatchedProductId.Value, out var purchaseHistory)
                        ? SupplierPriceComparisonPolicy.Evaluate(purchaseHistory)
                        : (SupplierPriceComparison?)null;

                return new ProductProfitabilityRowDto(
                    first.MatchedProductId ?? first.Group.NayaxProductId,
                    product?.Name ?? first.UnmappedName,
                    product?.CategoryName,
                    sales,
                    g.Sum(x => x.Group.Quantity),
                    row.CostOfGoods,
                    row.GrossProfit,
                    row.MarginPercent,
                    g.Sum(x => x.Group.TransactionCount),
                    isUnmapped,
                    !isUnmapped && isCogsComplete,
                    g.Sum(x => x.Group.CardRevenue),
                    g.Sum(x => x.Group.CashRevenue))
                {
                    PartialCostOfGoods = partialCost,
                    IsCogsComplete = isCogsComplete,
                    UncostedTransactionCount = g.Sum(x => x.Group.UncostedTransactionCount),
                    UncostedSalesAmount = g.Sum(x => x.Group.UncostedSalesAmount),
                    LastCost = purchaseCostComparison?.Latest?.UnitCost,
                    LastCostSupplierName = purchaseCostComparison?.Latest?.SupplierName,
                    LowestCost = purchaseCostComparison?.Lowest?.UnitCost,
                    LowestCostSupplierName = purchaseCostComparison?.Lowest?.SupplierName,
                    SavingPerUnit = purchaseCostComparison?.AbsoluteDifference
                };
            })
            .OrderByDescending(x => x.Sales)
            .ToList();

        var qualityNotes = new List<string>();
        if (rows.Any(x => x.IsUnmapped))
            qualityNotes.Add("One or more sales could not be mapped to a Product.");
        if (rows.Any(x => !x.IsCogsComplete))
            qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");

        // How well recorded provider coverage accounts for this period's stored sales (issue #731,
        // under issue #730's model), decided by the one Domain policy. Only the sales family: no
        // figure here comes from a settlement import.
        var salesAvailability = facts.Coverage.SalesAvailability();
        ReportAvailabilityNotes.Add(qualityNotes, salesAvailability, SalesFigures, ReportAvailabilityNotes.NoSalesData);

        var quality = ReportingQuality.Quality(
            missingStatus: true,
            historicalCostUnavailable: rows.Any(x => !x.IsCogsComplete),
            gstClassificationMissing: true,
            commissionNotPersisted: true,
            containsUnmappedProducts: rows.Any(x => x.IsUnmapped),
            notes: qualityNotes);

        return new ProductProfitabilityReportDto(range.From, range.ToDate, rows, quality)
        {
            SalesAvailability = salesAvailability
        };
    }

    /// <summary>
    /// The figures this report shows, named for its own columns. The product list itself is named
    /// too: an empty table is the figure a reader is most likely to misread as "nothing sold".
    /// </summary>
    public const string SalesFigures =
        "Each product's sales, quantity, cost of goods and gross profit, and the list of products itself";
}
