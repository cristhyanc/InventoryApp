namespace Inventory.Application.Purchases;

/// <summary>Narrow persistence port for purchase create/read/update/delete orchestration, owned by the Application layer.</summary>
public interface IPurchaseStore
{
    Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken);

    Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken);

    Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken);

    Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>
    /// The first product among <paramref name="productIds"/> whose inventory-cost transition
    /// cutoff <paramref name="purchaseDate"/> does not come after, or <c>null</c> when none conflicts.
    /// </summary>
    Task<(long ProductId, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the new purchase and its items, allocates any matching outstanding supplier-order
    /// fulfillment, creates their restock stock movements, and rebuilds affected inventory cost, as
    /// one transaction. The caller has already validated the items, the purchase date against
    /// cost-transition baselines, and the supplier.
    /// </summary>
    Task<PurchaseRecord> CreateAsync(
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        PurchaseFileMetadata file,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns <c>null</c> when no purchase with <paramref name="id"/> exists, or when
    /// <paramref name="fields"/>' supplier does not exist - the same not-found answer the former
    /// service gave both cases. Otherwise validates and applies the field/item changes, reconciles
    /// supplier-order fulfillment allocation and stock movements, and rebuilds affected inventory
    /// cost, as one transaction. Throws <see cref="InvalidOperationException"/> when an item is
    /// malformed/unknown or a cost-transition/preservation rule is violated.
    /// </summary>
    Task<PurchaseRecord?> UpdateAsync(
        int id,
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput>? items,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the purchase, its items, their stock movements, and any supplier-order fulfillment
    /// allocation, and rebuilds affected inventory cost, as one transaction. Returns <c>null</c>
    /// when no purchase with <paramref name="id"/> exists. Throws
    /// <see cref="InvalidOperationException"/> when a preserved movement would be removed.
    /// </summary>
    Task<string?> DeleteAsync(int id, CancellationToken cancellationToken);
}
