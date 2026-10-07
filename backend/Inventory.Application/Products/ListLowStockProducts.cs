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

        return SelectReorderAlerts(products, reorderNeeds);
    }

    /// <summary>
    /// The reorder-alert selection and ranking itself, over an already-read catalogue and an
    /// already-resolved <see cref="ReorderNeedsResult"/>. Exposed (issue #459) so the home Dashboard
    /// summary can count exactly this set from the catalogue and fleet read it has already made,
    /// instead of repeating the selection and fanning out to Nayax a second time: the count on the
    /// card and the rows on the reorder-alert list come from one implementation and cannot diverge.
    /// </summary>
    public static IReadOnlyList<ProductRecord> SelectReorderAlerts(
        IEnumerable<ProductRecord> products, ReorderNeedsResult reorderNeeds) =>
        products
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
