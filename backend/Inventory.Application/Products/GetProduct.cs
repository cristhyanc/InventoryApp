namespace Inventory.Application.Products;

/// <summary>
/// The single-product read use case, with its category, supplier and stock-adjustment detail.
/// Mirrors the former <c>InventoryApi.Services.ProductService.Get</c> exactly (issue #240): an id the
/// caller's business does not own answers <c>null</c>, which the API boundary maps to 404.
/// </summary>
public sealed class GetProduct
{
    private readonly IProductCatalogStore _catalog;

    public GetProduct(IProductCatalogStore catalog)
    {
        _catalog = catalog;
    }

    public Task<ProductRecord?> Handle(long id, CancellationToken cancellationToken) =>
        _catalog.FindAsync(id, cancellationToken);
}
