namespace Inventory.Application.Categories;

public sealed class ListCategories
{
    private readonly ICategoryStore _store;

    public ListCategories(ICategoryStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<CategoryRecord>> Handle(CancellationToken cancellationToken) =>
        _store.ListOrderedByNameAsync(cancellationToken);
}
