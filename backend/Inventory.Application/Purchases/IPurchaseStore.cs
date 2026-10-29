using Inventory.Application.SupplierOrders;

namespace Inventory.Application.Purchases;

/// <summary>Narrow persistence port for purchase create/read/update/delete orchestration, owned by the Application layer.</summary>
public interface IPurchaseStore
{
    Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken);

    Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// The purchase's stored-document metadata, or <c>null</c> when no purchase with
    /// <paramref name="id"/> exists <em>or</em> that purchase has no document at all (issue #976).
    /// The two are deliberately the same answer, so a caller cannot tell them apart.
    /// </summary>
    Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken);

    Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken);

    Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>
    /// The first product among <paramref name="productIds"/> whose inventory-cost transition
    /// cutoff <paramref name="purchaseDate"/> does not come after, or <c>null</c> when none conflicts.
    /// The product's name comes back with it so the refusal can name the product (issue #833); it
    /// is <c>null</c> when the product has no stored name.
    /// </summary>
    Task<(long ProductId, string? ProductName, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the new purchase and its items, allocates any matching outstanding supplier-order
    /// fulfillment, creates their restock stock movements, and rebuilds affected inventory cost, as
    /// one transaction. The caller has already validated the items, the purchase date against
    /// cost-transition baselines, and the supplier.
    ///
    /// <para><paramref name="supplierOrderCompletion"/>, when present, closes that order's
    /// remaining undelivered balance in the <em>same</em> transaction (issue #974). The
    /// implementation re-reads the order, re-checks its ownership, its eligibility and its current
    /// receipt totals inside that transaction and compares the decision's fingerprint against the
    /// one it recomputes; a mismatch - a competing receipt, a cancellation, an order that is no
    /// longer open, or a purchase that leaves nothing outstanding - throws
    /// <see cref="InvalidOperationException"/> and persists none of the purchase, its movements, its
    /// costing or the completion. It never changes a quantity, a cost or a stock movement for goods
    /// that were not received.</para>
    ///
    /// <para><paramref name="file"/> is <c>null</c> when the purchase has no supporting document
    /// (issue #976), and then all four of the purchase's document values are stored as <c>null</c>
    /// together. Nothing else about the write changes: the items, the allocations, the stock
    /// movements and the cost rebuild are identical with and without a document.</para>
    /// </summary>
    Task<PurchaseRecord> CreateAsync(
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        PurchaseFileMetadata? file,
        SupplierOrderCompletionDecision? supplierOrderCompletion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="document"/> on the purchase <paramref name="id"/> names, but
    /// <strong>only while that purchase has no document at all</strong> (issue #976).
    ///
    /// <para>The authoritative read, the emptiness check and the write are one operation: the
    /// implementation takes SQLite's write lock before it reads, so two concurrent attaches are
    /// serialised and the second one sees the first one's committed document and is refused with
    /// <see cref="PurchaseDocumentAttachOutcome.AlreadyHasDocument"/> rather than overwriting it.
    /// Replacing an existing document is out of scope, and a refusal writes nothing: the caller
    /// removes the bytes it had already stored.</para>
    /// </summary>
    Task<PurchaseDocumentAttachment> AttachDocumentAsync(
        int id, PurchaseFileMetadata document, CancellationToken cancellationToken);

    /// <summary>
    /// Returns <c>null</c> when no purchase with <paramref name="id"/> exists, or when
    /// <paramref name="fields"/>' supplier does not exist - the same not-found answer the former
    /// service gave both cases. Otherwise validates and applies the field/item changes, reconciles
    /// supplier-order fulfillment allocation and stock movements, and rebuilds affected inventory
    /// cost, as one transaction. Throws <see cref="InvalidOperationException"/> when an item is
    /// malformed/unknown or a cost-transition/preservation rule is violated, or when the purchase
    /// holds a receipt allocation on a supplier order that was completed with undelivered quantities
    /// (issue #974): reopening such an order is out of scope, so the correction is refused whole
    /// rather than silently detaching the receipts it was closed on.
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
    /// <see cref="InvalidOperationException"/> when a preserved movement would be removed, or when
    /// the purchase holds a receipt allocation on a supplier order that was completed with
    /// undelivered quantities (issue #974).
    /// </summary>
    Task<string?> DeleteAsync(int id, CancellationToken cancellationToken);
}
