namespace Inventory.Application.Categories;

/// <summary>
/// The category delete use case (issue #736). A category products still use is refused, with the
/// count in the message (<c>Inventory.Domain.Categories.CategoryCataloguePolicy.DeletionRefusal</c>),
/// because a product whose category disappears is quietly left uncategorised.
/// </summary>
public sealed class DeleteCategory
{
    private readonly ICategoryStore _store;

    public DeleteCategory(ICategoryStore store)
    {
        _store = store;
    }

    public Task<CategoryWriteAttempt> Handle(long id, CancellationToken cancellationToken) =>
        _store.DeleteAsync(id, cancellationToken);
}
