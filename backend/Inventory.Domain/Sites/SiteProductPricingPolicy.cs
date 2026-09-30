using Inventory.Domain.Reporting;

namespace Inventory.Domain.Sites;

/// <summary>
/// One machine-product mapping's price/quantity facts for a site product preview. <see cref="CommissionAmount"/>
/// is the card-sale site commission already resolved for <see cref="RetailPrice"/> by the caller
/// (<c>SiteCommissionCalculator.CommissionAmount</c> stays the one authoritative formula; this fact
/// only carries its result), and is meaningless when <see cref="RetailPrice"/> is null.
/// </summary>
public sealed record SiteProductPriceFact(
    long ProductId,
    decimal? RetailPrice,
    decimal CommissionAmount,
    int Par,
    int MissingStockByMdb);

/// <summary>The catalogue cost basis for a product eligible to appear in a site's product preview.</summary>
public sealed record SiteProductCostBasis(string Name, decimal AverageUnitCost, bool HasCostBasis);

public sealed record SiteProductPricingResult(
    long ProductId,
    string Name,
    decimal? AverageUnitCost,
    decimal SitePrice,
    decimal? EstimatedCardProfit,
    int QuantityInStock,
    int MaxStock);

/// <summary>
/// Site product preview pricing rule: the site's average retail price per catalogue product, and its
/// estimated per-unit card-sale profit net of commission, cost, and Nayax processing fee. Mirrors the
/// former <c>InventoryApi.Services.SiteService.AggregateProducts</c> private helper exactly (issue #241).
/// </summary>
public static class SiteProductPricingPolicy
{
    public static IReadOnlyList<SiteProductPricingResult> Calculate(
        IEnumerable<SiteProductPriceFact> facts,
        IReadOnlyDictionary<long, SiteProductCostBasis> costBasisByProductId,
        decimal? feeExGst,
        bool commissionConfigurationUnavailable)
    {
        var feeIncGst = feeExGst.HasValue
            ? feeExGst.Value + ReportingCalculations.GstFromExcluding(feeExGst.Value)
            : (decimal?)null;

        return facts
            .Where(fact => costBasisByProductId.ContainsKey(fact.ProductId))
            .GroupBy(fact => fact.ProductId)
            .Select(group =>
            {
                var costBasis = costBasisByProductId[group.Key];
                var items = group.ToList();
                var pricedItems = items.Where(item => item.RetailPrice.HasValue).ToList();
                var sitePrice = pricedItems.Count == 0
                    ? 0m
                    : pricedItems.Average(item => item.RetailPrice!.Value);
                decimal? estimatedCardProfit =
                    pricedItems.Count == 0 || feeIncGst is null || !costBasis.HasCostBasis || commissionConfigurationUnavailable
                        ? null
                        : pricedItems.Average(item =>
                            item.RetailPrice!.Value - item.CommissionAmount - costBasis.AverageUnitCost - feeIncGst.Value);

                return new SiteProductPricingResult(
                    group.Key,
                    costBasis.Name,
                    costBasis.HasCostBasis ? costBasis.AverageUnitCost : null,
                    sitePrice,
                    estimatedCardProfit,
                    items.Sum(item => item.Par - item.MissingStockByMdb),
                    items.Sum(item => item.Par));
            })
            .OrderBy(result => result.Name)
            .ToList();
    }
}
