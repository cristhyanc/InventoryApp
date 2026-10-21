using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The bulk product purchase-cost summary use case (issue #788, B of #786): the last and cheapest
/// recorded purchase cost of many products at once, so the product screens can show them for a whole
/// catalogue without one <c>GET api/products/{id}/price-history</c> request per product.
///
/// <para><b>No new last/cheapest formula is written here.</b> The history is read in one query
/// through <see cref="IProductPurchaseCostFactsProvider"/> (issue #207's bulk port, which the product
/// profitability report already uses) and each product's entries are handed to
/// <see cref="SupplierPriceComparisonPolicy"/> - the single authoritative calculation of issue #63,
/// including its tie-breaks: the last cost is the latest <c>PurchaseDate</c> and then the higher
/// <c>PurchaseItemId</c>, the cheapest is the lowest <c>UnitCost</c> and then the earliest
/// <c>PurchaseDate</c> and the lower <c>PurchaseItemId</c>. This use case selects nothing itself, so
/// <see cref="GetProductPriceComparison"/> and this summary cannot drift apart.</para>
///
/// <para>Scoping is the ordinary tenant query filters on the adapter behind that port; nothing here
/// takes or accepts a business.</para>
/// </summary>
public sealed class ListProductPurchaseCostSummaries
{
    private readonly IProductPurchaseCostFactsProvider _facts;

    public ListProductPurchaseCostSummaries(IProductPurchaseCostFactsProvider facts) => _facts = facts;

    /// <summary>
    /// One summary per product that has at least one recorded purchase line, in product id order.
    ///
    /// <para><paramref name="productIds"/> narrows the read to those products; an empty collection
    /// means the caller named none, which covers every product of the current business, retired
    /// products included. A product with no purchase history is absent from the result either way -
    /// it has no cost that was ever paid, which is not the same as a cost of zero.</para>
    /// </summary>
    public async Task<IReadOnlyList<ProductPurchaseCostSummaryDto>> Handle(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        var historyByProduct = productIds.Count == 0
            ? await _facts.GetForAllProductsAsync(cancellationToken).ConfigureAwait(false)
            : await _facts.GetForProductsAsync(productIds, cancellationToken).ConfigureAwait(false);

        var summaries = new List<ProductPurchaseCostSummaryDto>(historyByProduct.Count);

        foreach (var product in historyByProduct.OrderBy(entry => entry.Key))
        {
            var comparison = SupplierPriceComparisonPolicy.Evaluate(product.Value);

            // Both are set together by the policy for any non-empty history, and both absent for an
            // empty one. A product the port returned with no entries is therefore left out rather
            // than published with a fabricated cost of zero.
            if (comparison.Latest is not { } latest || comparison.Lowest is not { } lowest)
            {
                continue;
            }

            summaries.Add(new ProductPurchaseCostSummaryDto(
                product.Key,
                latest.UnitCost,
                latest.SupplierName,
                latest.PurchaseDate,
                lowest.UnitCost,
                lowest.SupplierName,
                lowest.PurchaseDate));
        }

        return summaries;
    }
}
