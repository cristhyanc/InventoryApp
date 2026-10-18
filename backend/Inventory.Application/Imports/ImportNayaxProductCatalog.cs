using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Providers;

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
///
/// The retail-price field this import reads is confirmed against the authoritative Nayax contract
/// (issue #363). A human confirmed from a live <c>GET /v1/operators/{OperatorID}/products</c>
/// response that the product selling price field is <c>ProductDefaultRetailPrice</c>, matching the
/// Nayax developer portal, which documents no bare <c>RetailPrice</c> on that endpoint
/// (<c>RetailPrice</c> is documented only on the machine-product endpoints, which
/// <c>NayaxMachineProduct</c> maps and which are unaffected). <c>NayaxProduct.ProductDefaultRetailPrice</c>
/// binds that confirmed JSON name. Products already imported with <c>UnitPrice</c> 0 under the
/// previous, unconfirmed mapping are not backfilled by this change (a separate decision).
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
    ///
    /// <para>Refused outright for a business that holds products the import cannot recognise
    /// (issue #736, parent #329 decision P2): see <see cref="ManualCatalogueImportPolicy"/> for why
    /// duplicating them is worse than refusing. The check runs before the two remote reads, so such
    /// a business spends no Nayax call and writes nothing, and the refusal is a
    /// <see cref="DomainConflictException"/> - a request that conflicts with the state of the data,
    /// which the HTTP boundary publishes as <c>409</c> with this message (docs/architecture.md
    /// § Domain and application error mapping).</para>
    /// </summary>
    public async Task Handle(CancellationToken cancellationToken)
    {
        var productsWithNoProviderId = await _store.CountProductsWithNoProviderIdAsync(cancellationToken);
        if (ManualCatalogueImportPolicy.Refusal(productsWithNoProviderId) is { } refusal)
            throw new DomainConflictException(refusal);

        // Two independent remote reads, so they stay a concurrent fan-out. Each resolves the
        // business's Nayax credentials through the request's scoped context (issue #520), and those
        // two reads are serialized by EfNayaxConnectionStore rather than by holding the remote
        // requests apart; the import's own store calls follow, one at a time
        // (docs/architecture.md § Concurrency inside one request).
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
                product.ProductDefaultRetailPrice ?? 0m,
                product.ProductGroupId));
        }

        await _store.ApplyAsync(
            new NayaxProductCatalogImport(categories, products, _clock.UtcNow),
            cancellationToken);
    }
}
