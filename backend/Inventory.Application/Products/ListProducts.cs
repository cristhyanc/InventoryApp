namespace Inventory.Application.Products;

/// <summary>
/// The product listing use case: the filtered catalogue ordered by name, or - when the caller asks
/// for low stock only - whatever <see cref="ListLowStockProducts"/> answers for the same filter.
/// Mirrors the former <c>InventoryApi.Services.ProductService.GetAll</c> exactly (issue #240),
/// including that <c>lowStockOnly</c> replaces the plain listing rather than narrowing it, so the
/// reorder-alert ordering wins over the by-name ordering.
///
/// <para>Because <c>lowStockOnly</c> is the reorder-alert listing, the answer carries that listing's
/// <see cref="Inventory.Application.Reorder.ReorderMode"/> and reason too (issue #726). The plain
/// catalogue listing makes no reorder calculation, so it carries neither.</para>
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

    public async Task<ProductListing> Handle(
        ProductCatalogFilter filter, bool? lowStockOnly, CancellationToken cancellationToken) =>
        lowStockOnly == true
            ? await _lowStockProducts.Handle(filter, cancellationToken)
            : ProductListing.Catalogue(await _catalog.ListOrderedByNameAsync(filter, cancellationToken));
}
