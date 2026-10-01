namespace Inventory.Application.Products;

/// <summary>
/// The optional search/category/supplier narrowing a product listing request carries. A null member
/// means "do not narrow on this", exactly as the former
/// <c>InventoryApi.Services.ProductService.GetAll</c>/<c>LowStock</c> query building did.
/// </summary>
public sealed record ProductCatalogFilter(string? Search, long? CategoryId, int? SupplierId)
{
    /// <summary>The unnarrowed whole catalogue.</summary>
    public static ProductCatalogFilter None { get; } = new(null, null, null);
}

/// <summary>
/// Narrow read port for the product catalogue, owned by the Application layer (issue #240). Reads
/// are tenant-scoped centrally by the persistence adapter's own query filters, never by a filter
/// member here: no caller may choose an owning business (AGENTS.md § Tenant ownership).
/// </summary>
public interface IProductCatalogStore
{
    /// <summary>
    /// The filtered catalogue ordered by name, with category, supplier and stock-adjustment detail.
    /// </summary>
    Task<IReadOnlyList<ProductRecord>> ListOrderedByNameAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// The filtered catalogue in no particular order, for a caller that ranks or enriches the rows
    /// itself (<see cref="ListLowStockProducts"/>, <c>Inventory.Application.Machines.ListMachineProducts</c>).
    /// </summary>
    Task<IReadOnlyList<ProductRecord>> ListUnorderedAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken);

    /// <summary>The single product, or <c>null</c> when the caller's business owns no such product.</summary>
    Task<ProductRecord?> FindAsync(long id, CancellationToken cancellationToken);
}
