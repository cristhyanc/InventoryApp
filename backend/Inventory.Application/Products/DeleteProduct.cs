namespace Inventory.Application.Products;

public sealed class DeleteProduct
{
    private readonly IProductStore _store;

    public DeleteProduct(IProductStore store)
    {
        _store = store;
    }

    public Task<bool> Handle(long id, CancellationToken cancellationToken) => _store.DeleteAsync(id, cancellationToken);
}
