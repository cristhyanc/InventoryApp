namespace Inventory.Application.Categories;

public sealed class GetCategory
{
    private readonly ICategoryStore _store;

    public GetCategory(ICategoryStore store)
    {
        _store = store;
    }

    public Task<CategoryRecord?> Handle(long id, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(id, cancellationToken);
}
