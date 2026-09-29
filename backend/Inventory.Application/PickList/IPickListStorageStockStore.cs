namespace Inventory.Application.PickList;

/// <summary>One product's name and physical storage quantity, as needed by the Pick List projection.</summary>
public record PickListStorageProduct(long ProductId, string ProductName, int QuantityInStock);

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
