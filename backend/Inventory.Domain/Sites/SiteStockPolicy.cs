namespace Inventory.Domain.Sites;

/// <summary>
/// One machine-product mapping's stock facts, aggregated across every machine at a site. Carries no
/// query or persistence behavior; a caller resolves <see cref="ProductId"/>/<see cref="ProductIsActive"/>
/// from the catalogue before building this fact.
/// </summary>
public sealed record SiteMachineProductStockFact(
    long? ProductId,
    bool ProductIsActive,
    int Par,
    int MissingStockByMdb,
    int VendOutAlertThreshold);

/// <summary>
/// Site dashboard stock rules: the overall stock percentage across every machine at a site, and the
/// low/empty product alert counts. Mirrors the former <c>InventoryApi.Services.SiteService</c>
/// <c>GetStockTotals</c>/<c>GetStockCounts</c> private helpers exactly (issue #241).
/// </summary>
public static class SiteStockPolicy
{
    /// <summary>
    /// The site's overall stock percentage, across every machine-product mapping regardless of
    /// whether it resolves to a known/active catalogue product - unmapped stock still counts toward
    /// physical capacity. 100% when the site reports no capacity at all.
    /// </summary>
    public static decimal CalculateStockPercentage(IEnumerable<SiteMachineProductStockFact> facts)
    {
        var totals = facts.Aggregate(
            (QuantityInStock: 0, MaxStock: 0),
            (totals, fact) => (
                totals.QuantityInStock + fact.Par - fact.MissingStockByMdb,
                totals.MaxStock + fact.Par));

        return totals.MaxStock == 0 ? 100 : (decimal)totals.QuantityInStock / totals.MaxStock * 100;
    }

    /// <summary>
    /// Counts how many distinct active catalogue products are low (in stock but at/under their
    /// combined vend-out alert threshold) or empty (at or under zero), summed across every machine at
    /// the site that carries the product.
    /// </summary>
    public static (int LowProductCount, int EmptyProductCount) CalculateAlertCounts(
        IEnumerable<SiteMachineProductStockFact> facts)
    {
        var byProduct = facts
            .Where(fact => fact.ProductId.HasValue && fact.ProductIsActive)
            .GroupBy(fact => fact.ProductId!.Value)
            .Select(group => new
            {
                Quantity = group.Sum(fact => fact.Par - fact.MissingStockByMdb),
                Threshold = group.Sum(fact => fact.VendOutAlertThreshold)
            });

        return (
            byProduct.Count(item => item.Quantity > 0 && item.Quantity <= item.Threshold),
            byProduct.Count(item => item.Quantity <= 0));
    }
}
