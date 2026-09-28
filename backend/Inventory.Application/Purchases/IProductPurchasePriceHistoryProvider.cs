namespace Inventory.Application.Purchases;

/// <summary>
/// Narrow Application-owned port for a product's actual Purchase-item cost history, scoped to the
/// caller's business by the persistence adapter's tenant-filtered context. Derives history from
/// immutable Purchase items rather than a separate quoted-price record, per issue #63.
/// </summary>
public interface IProductPurchasePriceHistoryProvider
{
    Task<IReadOnlyList<SupplierPriceHistoryFact>> GetForProductAsync(long productId, CancellationToken cancellationToken);
}
