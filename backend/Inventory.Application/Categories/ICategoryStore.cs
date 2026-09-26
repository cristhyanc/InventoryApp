namespace Inventory.Application.Categories;

/// <summary>
/// Narrow persistence port for product categories, owned by the Application layer.
/// </summary>
public interface ICategoryStore
{
    Task<IReadOnlyList<CategoryRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    Task<CategoryRecord?> FindByIdAsync(long id, CancellationToken cancellationToken);
}
