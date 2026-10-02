namespace Inventory.Application.Purchases;

public sealed class GetPurchase
{
    private readonly IPurchaseStore _store;

    public GetPurchase(IPurchaseStore store)
    {
        _store = store;
    }

    public Task<PurchaseRecord?> Handle(int id, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(id, cancellationToken);
}
