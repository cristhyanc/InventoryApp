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

    public Task<PurchaseRecord?> Handle(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken) =>
        _store.UpdateAsync(id, fields, items, cancellationToken);
}
