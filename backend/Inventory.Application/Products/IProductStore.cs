namespace Inventory.Application.Products;

/// <summary>
/// Narrow persistence port for product create/update/delete orchestration, owned by the
/// Application layer.
/// </summary>
public interface IProductStore
{
    /// <summary>
    /// Persists the new product and, when its quantity is positive, the initial restock stock
    /// adjustment and (when an initial unit cost is given) an inventory cost rebuild. Returns the
    /// new product's id.
    /// </summary>
    Task<long> CreateAsync(ProductCreateFields fields, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(long id, CancellationToken cancellationToken);

    /// <summary>Updates the product's editable fields. Assumes the product's existence was already confirmed.</summary>
    Task UpdateAsync(long id, ProductUpdateFields fields, CancellationToken cancellationToken);

    /// <summary>Deletes the product. Returns <c>false</c> when no product with <paramref name="id"/> exists.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}
