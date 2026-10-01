using Inventory.Application.Reorder;

namespace Inventory.Application.Products;

/// <summary>
/// The reorder-alert listing use case: takes the filtered catalogue, enriches each product with the
/// live machine replenishment need and outstanding supplier-order quantity resolved by
/// <see cref="CalculateReorderNeeds"/> (issue #47), keeps only the products that alert, and ranks
/// them by how much must be ordered. Mirrors the former
/// <c>InventoryApi.Services.ProductService.LowStock</c> exactly (issue #240), including the negative
/// outstanding-quantity clamp and the <c>NeedToOrder</c> descending, then name ascending, order.
///
/// The alert test and the ranking value both come from
/// <see cref="Inventory.Domain.Products.ProductReorderPolicy"/>, the same formulas the API's product
/// response exposes, so a product can never be listed with a reorder quantity the caller then sees
/// differently.
/// </summary>
public sealed class ListLowStockProducts
{
    private readonly IProductCatalogStore _catalog;
    private readonly CalculateReorderNeeds _calculateReorderNeeds;

    public ListLowStockProducts(IProductCatalogStore catalog, CalculateReorderNeeds calculateReorderNeeds)
    {
        _catalog = catalog;
        _calculateReorderNeeds = calculateReorderNeeds;
    }

    public async Task<IReadOnlyList<ProductRecord>> Handle(
        ProductCatalogFilter filter, CancellationToken cancellationToken)
    {
        var products = await _catalog.ListUnorderedAsync(filter, cancellationToken);

        var reorderNeeds = await _calculateReorderNeeds.Handle(cancellationToken);

        return products
            .Select(product => product with
            {
                MachineReplenishmentNeed =
                    reorderNeeds.MachineReplenishmentNeedByProductId.GetValueOrDefault(product.Id),
                OnOrderQuantity =
                    Math.Max(0m, reorderNeeds.OnOrderQuantityByProductId.GetValueOrDefault(product.Id)),
            })
            .Where(product => product.IsReorderAlert)
            .OrderByDescending(product => product.NeedToOrder)
            .ThenBy(product => product.Name)
            .ToList();
    }
}
