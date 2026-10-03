namespace Inventory.Application.Costing;

/// <summary>The product facts an inventory-cost transition preview is built from.</summary>
public sealed record InventoryCostTransitionProduct(long Id, string Name, int QuantityInStock, decimal AverageUnitCost);

/// <summary>Whether a stored preview covers one product or every eligible product.</summary>
public enum InventoryCostTransitionPreviewScope
{
    SingleProduct,
    AllProducts
}

/// <summary>
/// A preview snapshot to store. <paramref name="ProductId"/> is the previewed product, or <c>0</c>
/// for an all-products preview, exactly as the persisted draft has always recorded it.
/// </summary>
public sealed record NewInventoryCostTransitionDraft(
    Guid Id,
    long ProductId,
    string SnapshotJson,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>A stored preview snapshot, as loaded for apply.</summary>
public sealed record StoredInventoryCostTransitionDraft(
    Guid Id,
    string SnapshotJson,
    DateTime ExpiresAt,
    DateTime? AppliedAt);

/// <summary>
/// The database transaction an apply runs in. Disposing it without <see cref="CommitAsync"/> rolls
/// back every change saved through the store since it began.
/// </summary>
public interface IInventoryCostTransitionTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the inventory-cost transition use cases (issue #298, child 4 of
/// #149), owned by the Application layer. Its implementation reads and writes through the caller's
/// business scope and applies no transition rule of its own; it shares its unit of work with the
/// <see cref="IRebuildProductCost"/> rebuild, so <see cref="SaveChangesAsync"/> also persists what a
/// rebuild staged.
/// </summary>
public interface IInventoryCostTransitionStore
{
    /// <summary>Begins the transaction a single or all-products apply runs in.</summary>
    Task<IInventoryCostTransitionTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Whether any of the products already has a transition baseline.</summary>
    Task<bool> AnyBaselineAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>The product, or <c>null</c> when it does not exist (or is not the caller's).</summary>
    Task<InventoryCostTransitionProduct?> GetProductAsync(long productId, CancellationToken cancellationToken);

    /// <summary>Every product without a transition baseline, ordered by name.</summary>
    Task<IReadOnlyList<InventoryCostTransitionProduct>> ListProductsWithoutBaselineAsync(CancellationToken cancellationToken);

    /// <summary>The existing products among <paramref name="productIds"/>, ordered by name.</summary>
    Task<IReadOnlyList<InventoryCostTransitionProduct>> ListProductsAsync(
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Each product's legacy physical replay: the sum of its stock movements effective at or before
    /// <paramref name="cutoffAt"/>. A product with no such movement is absent.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> SumPhysicalMovementsAsync(
        IReadOnlyCollection<long> productIds,
        DateTime cutoffAt,
        CancellationToken cancellationToken);

    /// <summary>Stages a new preview snapshot.</summary>
    void AddDraft(NewInventoryCostTransitionDraft draft);

    /// <summary>The stored preview of the given scope, loaded so it can be marked applied.</summary>
    Task<StoredInventoryCostTransitionDraft?> FindDraftAsync(
        Guid previewId,
        InventoryCostTransitionPreviewScope scope,
        CancellationToken cancellationToken);

    /// <summary>Stages the applied time on a preview loaded by <see cref="FindDraftAsync"/>.</summary>
    void MarkDraftApplied(Guid previewId, DateTime appliedAt);

    /// <summary>Stages the product's transition baseline, with its per-machine stock, exactly as previewed.</summary>
    void AddBaseline(InventoryCostTransitionPreview preview);

    /// <summary>Persists every staged change.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
