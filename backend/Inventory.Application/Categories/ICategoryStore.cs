namespace Inventory.Application.Categories;

/// <summary>
/// Narrow persistence port for product categories, owned by the Application layer.
/// </summary>
public interface ICategoryStore
{
    Task<IReadOnlyList<CategoryRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    Task<CategoryRecord?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the category, unless this business already has one with that name (issue #736). The
    /// look for a clash and the insert are one operation, because a uniqueness rule the database
    /// does not hold is only as good as the window between them.
    /// </summary>
    Task<CategoryWriteAttempt> CreateAsync(CategoryWriteFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the category, after reading the stored row and deciding the refusals that depend on
    /// it: a rename of a category the business's sales provider manages, and a name another category
    /// of this business already uses. The read, the comparison and the write are one operation, so a
    /// name taken between a caller's read and this write is still refused.
    /// </summary>
    Task<CategoryWriteAttempt> UpdateAsync(
        long id, CategoryWriteFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the category only while no product uses it
    /// (<c>Inventory.Domain.Categories.CategoryCataloguePolicy.DeletionRefusal</c>); otherwise it
    /// reports that refusal, including how many products use it, and writes nothing. Counting and
    /// deleting are one operation, because a product's category is set to nothing when its category
    /// disappears.
    /// </summary>
    Task<CategoryWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken);
}
