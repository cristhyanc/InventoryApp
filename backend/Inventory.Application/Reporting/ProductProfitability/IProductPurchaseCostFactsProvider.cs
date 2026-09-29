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
}
