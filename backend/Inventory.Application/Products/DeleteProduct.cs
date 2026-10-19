namespace Inventory.Application.Products;

/// <summary>
/// The product delete use case. Since issue #736 a delete can be <em>refused</em>: a product that
/// anything already refers to, or that the business's sales provider manages, is retired through
/// <c>IsActive</c> instead of deleted, so no inventory or financial history is lost
/// (<c>Inventory.Domain.Products.ProductDeletionPolicy</c>). The counting and the delete happen as
/// one operation in the store, which is what makes the refusal reliable for the two references that
/// cascade.
/// </summary>
public sealed class DeleteProduct
{
    private readonly IProductStore _store;

    public DeleteProduct(IProductStore store)
    {
        _store = store;
    }

    public Task<ProductWriteAttempt> Handle(long id, CancellationToken cancellationToken) =>
        _store.DeleteAsync(id, cancellationToken);
}
