namespace Inventory.Application.Products;

/// <summary>
/// The product listing use case: the filtered catalogue ordered by name, or - when the caller asks
/// for low stock only - whatever <see cref="ListLowStockProducts"/> answers for the same filter.
/// Mirrors the former <c>InventoryApi.Services.ProductService.GetAll</c> exactly (issue #240),
/// including that <c>lowStockOnly</c> replaces the plain listing rather than narrowing it, so the
/// reorder-alert ordering wins over the by-name ordering.
/// </summary>
public sealed class ListProducts
{
    private readonly IProductCatalogStore _catalog;
    private readonly ListLowStockProducts _lowStockProducts;

    public ListProducts(IProductCatalogStore catalog, ListLowStockProducts lowStockProducts)
    {
        _catalog = catalog;
        _lowStockProducts = lowStockProducts;
    }

    public Task<IReadOnlyList<ProductRecord>> Handle(
        ProductCatalogFilter filter, bool? lowStockOnly, CancellationToken cancellationToken) =>
        lowStockOnly == true
            ? _lowStockProducts.Handle(filter, cancellationToken)
            : _catalog.ListOrderedByNameAsync(filter, cancellationToken);
}
