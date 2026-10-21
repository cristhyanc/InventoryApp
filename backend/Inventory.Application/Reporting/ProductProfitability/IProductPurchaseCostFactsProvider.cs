using Inventory.Domain.Purchases;

namespace Inventory.Application.Reporting.ProductProfitability;

/// <summary>
/// Narrow Application-owned port for the actual Purchase-item cost history of many catalogue
/// products at once (issue #207), so the product profitability report can derive its Last/Lowest
/// Cost purchasing insights without an N+1 per-product history query. Returns each requested
/// product's raw <see cref="SupplierPriceHistoryEntry"/> list only - the Domain
/// <c>SupplierPriceComparisonPolicy</c> from issue #63, the single authoritative lowest/latest-cost
/// algorithm, is applied by <see cref="GetProductProfitabilityReport"/>, never by this port or its
/// adapter. A product with no requested id or no recorded history is simply absent from the result.
/// </summary>
public interface IProductPurchaseCostFactsProvider
{
    Task<IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>> GetForProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>
    /// The same projection for <b>every</b> product of the current business, retired products
    /// included (issue #788, B of #786). It is a separate read rather than an empty id list,
    /// because an empty list means "no products were asked about" and must keep answering nothing:
    /// <c>Inventory.Application.Purchases.ListProductPurchaseCostSummaries</c> needs the
    /// whole-business read for the unfiltered product screens, and conflating the two would make
    /// one caller's empty filter silently read the entire history.
    ///
    /// <para>A product with no recorded purchase line is absent, exactly as it is above - there is
    /// no row to summarise, not a zero cost - so this answers purchase history rather than a
    /// catalogue, and needs no product listing beside it.</para>
    /// </summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>> GetForAllProductsAsync(
        CancellationToken cancellationToken);
}
