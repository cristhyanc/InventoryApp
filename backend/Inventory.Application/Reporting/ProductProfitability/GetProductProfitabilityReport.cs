using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Domain.Reporting.Profitability;

namespace Inventory.Application.Reporting.ProductProfitability;

/// <summary>
/// The product profitability report use case: resolves the requested date range/machine scope,
/// retrieves report facts through the narrow <see cref="IProductProfitabilityReportFactsProvider"/>
/// port, matches each raw Nayax product identifier/name group to the catalogue through the
/// deterministic <c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c>, merges groups
/// that resolve to the same product (or the same unmapped identity), applies the shared Domain
/// profit/margin/completeness policy, and builds the authoritative
/// <see cref="ProductProfitabilityReportDto"/> consumed by both the API response and the
/// CSV/XLSX export.
/// </summary>
public sealed class GetProductProfitabilityReport : IGetProductProfitabilityReport
{
    private readonly IProductProfitabilityReportFactsProvider _facts;

    public GetProductProfitabilityReport(IProductProfitabilityReportFactsProvider facts)
    {
        _facts = facts;
    }

    public async Task<ProductProfitabilityReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var range = ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);
        var machineId = filter.MachineId ?? filter.MachineID;

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, machineId, cancellationToken);
        var catalogueById = facts.Catalogue.ToDictionary(x => x.Id);
        var candidates = facts.Catalogue.Select(x => new ProductMatchCandidate(x.Id, x.Name)).ToArray();

        var matched = facts.SaleGroups.Select(group =>
        {
            var matchedProductId = ProductMatcher.Match(candidates, group.NayaxProductId, group.ProductName);
            var unmappedName = string.IsNullOrWhiteSpace(group.ProductName)
                ? "Unmapped product"
                : ProductMatcher.NormalizeName(group.ProductName);
            return (Group: group, MatchedProductId: matchedProductId, UnmappedName: unmappedName);
        }).ToList();

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
                    UncostedSalesAmount = g.Sum(x => x.Group.UncostedSalesAmount)
                };
            })
            .OrderByDescending(x => x.Sales)
            .ToList();

        var qualityNotes = new List<string>();
        if (rows.Any(x => x.IsUnmapped))
            qualityNotes.Add("One or more sales could not be mapped to a Product.");
        if (rows.Any(x => !x.IsCogsComplete))
            qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");

        var quality = ReportingQuality.Quality(
            missingStatus: true,
            historicalCostUnavailable: rows.Any(x => !x.IsCogsComplete),
            gstClassificationMissing: true,
            commissionNotPersisted: true,
            containsUnmappedProducts: rows.Any(x => x.IsUnmapped),
            notes: qualityNotes);

        return new ProductProfitabilityReportDto(range.From, range.ToDate, rows, quality);
    }
}
