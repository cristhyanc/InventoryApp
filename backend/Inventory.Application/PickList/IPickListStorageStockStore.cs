namespace Inventory.Application.PickList;

/// <summary>
/// One product's name and physical storage quantity, as needed by the Pick List projection.
/// <paramref name="IsActive"/> is the catalogue's retirement flag, carried as a fact so the rule
/// that a retired product is not picked stays in the use case rather than in the query - the same
/// shape <c>Inventory.Domain.Sites.SiteStockPolicy</c> and
/// <c>Inventory.Domain.Machines.MachineRefillAlertPolicy</c> already use (issue #736).
/// </summary>
public record PickListStorageProduct(long ProductId, string ProductName, int QuantityInStock, bool IsActive);

/// <summary>
/// Narrow persistence port for the Pick List projection's storage-quantity lookup (issue #221),
/// bounded by the caller's own product IDs rather than the whole catalogue. Deliberately its own
/// port rather than a reuse of <see cref="Inventory.Application.MachineStockSync.IMachineStockEventStore"/>:
/// that store's contract is the Sync Restock bounded context (imports, refill application, duplicate
/// resolution) and this read-only query needs none of it.
/// </summary>
public interface IPickListStorageStockStore
{
    Task<IReadOnlyDictionary<long, PickListStorageProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);
}
