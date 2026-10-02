namespace Inventory.Application.Purchases;

public sealed class ListPurchases
{
    private readonly IPurchaseStore _store;

    public ListPurchases(IPurchaseStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<PurchaseRecord>> Handle(int? supplierId, CancellationToken cancellationToken) =>
        _store.ListAsync(supplierId, cancellationToken);
}
