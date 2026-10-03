using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Application.Imports;

/// <summary>
/// The Nayax product catalogue import use case (issue #300, child 2 of 3 of #151), moved unchanged
/// in behaviour out of <c>InventoryApi.Services.ImportService.ImportProductsAsync</c>: read the
/// operator's products and product groups through the <see cref="INayaxLynxClient"/> port, project
/// them onto the catalogue fields this import owns, and apply the whole snapshot through
/// <see cref="INayaxProductCatalogImportStore"/>.
///
/// The catalogue is a one-way sync of raw remote facts, so this slice derives no accounting value
/// and adds no <c>Inventory.Domain</c> rule. The one financial semantic it carries is
/// <c>Product.UnitPrice</c>: the catalogue retail price is a display/default selling price and the
/// Nayax <c>ProductCostPrice</c> is a cost, so the two must never be conflated (AGENTS.md
/// § Inventory and historical costing invariants; docs/architecture.md § Product selling price).
/// </summary>
public sealed class ImportNayaxProductCatalog
{
    private readonly INayaxLynxClient _nayax;
    private readonly INayaxProductCatalogImportStore _store;
    private readonly IClock _clock;

    public ImportNayaxProductCatalog(
        INayaxLynxClient nayax,
        INayaxProductCatalogImportStore store,
        IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _clock = clock;
    }

    /// <summary>
    /// Imports the operator's current catalogue. A failed Nayax read propagates, so a partial
    /// remote snapshot can never be applied as a complete one.
    /// </summary>
    public async Task Handle(CancellationToken cancellationToken)
    {
        // Two independent remote reads, so they stay a concurrent fan-out; neither touches the
        // request's scoped persistence (docs/architecture.md § Concurrency inside one request).
        var productsTask = _nayax.GetProductsAsync(cancellationToken);
        var groupsTask = _nayax.GetProductGroupssAsync(cancellationToken);
        await Task.WhenAll(productsTask, groupsTask);

        var categories = new List<ImportedProductCategory>();
        foreach (var group in await groupsTask)
        {
            // A group with no identifier cannot key a category, and a blank name cannot name one.
            if (group.ProductGroupID is not int groupId || string.IsNullOrWhiteSpace(group.ProductGroupName))
                continue;
            categories.Add(new ImportedProductCategory(groupId, group.ProductGroupName, group.ProductGroupName));
        }

        var products = new List<ImportedProductCatalogEntry>();
        foreach (var product in await productsTask)
        {
            products.Add(new ImportedProductCatalogEntry(
                product.NayaxProductId,
                product.ProductName!,
                product.ProductDescription,
                product.RetailPrice ?? 0m,
                product.ProductGroupId));
        }

        await _store.ApplyAsync(
            new NayaxProductCatalogImport(categories, products, _clock.UtcNow),
            cancellationToken);
    }
}
