namespace Inventory.Application.Purchases;

/// <summary>
/// The purchase edit use case. The field/item validation, the supplier-order fulfillment
/// reconciliation, the stock-movement diffing, and the resulting inventory-cost rebuild are a
/// single transaction against the tracked purchase entity graph, so they stay in
/// <see cref="IPurchaseStore"/>'s EF-owned implementation (docs/architecture.md's Purchasing and
/// costing slice), the same carve-out the categories/suppliers and operating-expenses slices used
/// for their own EF-coupled writes.
/// </summary>
public sealed class UpdatePurchase
{
    private readonly IPurchaseStore _store;

    public UpdatePurchase(IPurchaseStore store)
    {
        _store = store;
    }

    /// <exception cref="InvalidOperationException">
    /// A submitted GST classification is unsupported, the submitted invoice number is too long, or
    /// the store rejects the edit.
    /// </exception>
    public Task<PurchaseRecord?> Handle(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken)
    {
        // Checked here rather than in the store, so an unsupported classification is refused before
        // the edit's transaction opens and nothing is written (issue #429).
        PurchaseGstSubmission.EnsureClassificationsAreSupported(fields, items);

        // Likewise for the invoice number (issue #1162): an over-long one is refused before the
        // transaction opens, and the accepted one reaches the store trimmed, with blank as none - so
        // an edit that clears it stores no number rather than an empty string.
        return _store.UpdateAsync(id, PurchaseInvoiceNumberSubmission.Normalized(fields), items, cancellationToken);
    }
}
