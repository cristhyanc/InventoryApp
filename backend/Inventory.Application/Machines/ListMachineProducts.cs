using Inventory.Application.Nayax;
using Inventory.Application.Products;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine product listing use case: matches the machine's live Nayax product mappings against
/// the local catalogue, overlays the machine-specific price/commission/MDB/stock facts, and resolves
/// each row's suggested net/price values through <see cref="ResolveMachineProductPricing"/>. Mirrors
/// the former <c>InventoryApi.Services.MachineService.GetMachineProducts</c> (issue #240): a Nayax
/// mapping with no matching local product is dropped, and the result is ordered by MDB code.
///
/// <para><b>The machine's site comes from the machine list</b> (issue #846, step 2 of #839). That
/// read is this use case's only use for the machine itself, and the page that shows this listing has
/// already read the list through the cache of issue #840 - so opening a machine costs one Nayax
/// machine read rather than two. <see cref="ResolveSiteAsync"/> states what that means for a machine
/// the list does not hold, for one that has moved site, and for an account Nayax has not granted the
/// fleet list to - which is served by the read this listing used to make, so no answer of this
/// endpoint needs a permission it did not need before.</para>
///
/// The match itself goes through the Nayax link table (issue #733): a mapping's
/// <c>NayaxProductID</c> is resolved to a local product id by
/// <see cref="INayaxProductLinkStore"/> and only then looked up in the catalogue, instead of being
/// compared with <c>Product.Id</c>. A mapping this business holds no link for is dropped, exactly as
/// an unmatched one already was.
/// </summary>
public sealed class ListMachineProducts
{
    private readonly INayaxLynxClient _nayax;
    private readonly IProductCatalogStore _catalog;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly ResolveMachineProductPricing _resolvePricing;

    public ListMachineProducts(
        INayaxLynxClient nayax,
        IProductCatalogStore catalog,
        INayaxProductLinkStore productLinks,
        ResolveMachineProductPricing resolvePricing)
    {
        _nayax = nayax;
        _catalog = catalog;
        _productLinks = productLinks;
        _resolvePricing = resolvePricing;
    }

    public async Task<IReadOnlyList<MachineProductRecord>> Handle(long machineId, CancellationToken cancellationToken)
    {
        var siteId = await ResolveSiteAsync(machineId, cancellationToken);
        var nayaxMachineProducts = await _nayax.GetMachineProductsAsync(machineId, cancellationToken);
        var catalogue = await _catalog.ListUnorderedAsync(ProductCatalogFilter.None, cancellationToken);
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            nayaxMachineProducts
                .Where(machineProduct => machineProduct.NayaxProductID.HasValue)
                .Select(machineProduct => machineProduct.NayaxProductID!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);
        var catalogueById = catalogue.ToDictionary(product => product.Id);

        var rows = nayaxMachineProducts
            .Select(machineProduct =>
            {
                var source = LocalProduct(machineProduct.NayaxProductID, localProductIdByNayaxId, catalogueById);
                return source is null
                    ? null
                    : new MachineProductRecord
                    {
                        Product = source,
                        MachinePrice = machineProduct.RetailPrice ?? 0m,
                        CommissionValue = machineProduct.CommissionValue ?? 0m,
                        MdbCode = machineProduct.MDBCode,
                        QuantityInStock = (machineProduct.PAR - machineProduct.MissingStockByMDB) ?? 0,
                        MaxStockInMachine = machineProduct.PAR,
                    };
            })
            .Where(row => row is not null)
            .Cast<MachineProductRecord>()
            .ToList();

        // Positional, not keyed by product id: more than one machine slot can list the same
        // product with a different machine price, so a dictionary keyed by id would collide.
        var pricingFacts = rows
            .Select(row => new MachineProductPricingFact(row.Product.Id, row.MachinePrice, row.Product.AverageUnitCost))
            .ToList();
        var pricingResults = await _resolvePricing.Handle(siteId, pricingFacts, cancellationToken);

        return rows
            .Select((row, index) => row with
            {
                SuggestedNetValue = pricingResults[index].SuggestedNetValue,
                SuggestedPriceValue = pricingResults[index].SuggestedPriceValue,
            })
            .OrderBy(row => row.MdbCode)
            .ToList();
    }

    /// <summary>
    /// The site this machine is at - the <c>CustomerID</c> its machine-list entry carries - or
    /// <see langword="null"/> when it is at none (issue #846).
    ///
    /// <para>It comes from <see cref="INayaxLynxClient.GetMachinesAsync"/>, which the machine page has
    /// already read through the machine-list cache of issue #840, so opening a machine makes one
    /// Nayax machine read across both of the page's requests instead of two: this listing used to
    /// read the machine itself purely for this one field, which
    /// <see cref="GetMachineDashboard"/> reads on the same page. A listed machine with no
    /// <c>CustomerID</c> is at no site and is priced with none - that is the list's answer, not a
    /// reason to ask again.</para>
    ///
    /// <para>A machine the list does not hold - added in Nayax within the cached list's ten minutes,
    /// or unknown to Nayax at all - is read on its own, exactly as it was before, and before the
    /// machine products are read: so an unknown machine still fails as the same controlled upstream
    /// failure, and the accepted cost of the cache is that a machine moved to another site keeps the
    /// previous site's pricing until the list expires.</para>
    ///
    /// <para><b>A fleet list Nayax has not granted permission for is answered by the read this
    /// listing used to make</b>, not by a refusal. Issue #846 buys one machine read; it was not
    /// allowed to buy a new permission. A Nayax account may grant
    /// <c>GET machines/{id}</c> and <c>GET machines/{id}/machineProducts</c> while refusing
    /// <c>GET machines</c>, and that is exactly the account whose connection the test of issue #536
    /// concludes <see cref="Domain.Nayax.NayaxConnectionStatus.PendingPermissions"/> for - which is
    /// what makes its <c>403</c> the classified per-feature
    /// <see cref="NayaxPermissionNotGrantedException"/> rather than an upstream failure. Its listing
    /// worked before this change and still works, priced by the machine's own record.</para>
    ///
    /// <para><b>That one classified refusal is the only thing caught here.</b> Every other way the
    /// fleet read can fail propagates untouched, because none of them says the fleet list is
    /// unavailable to this business: <see cref="NayaxNotConnectedException"/> (no credentials an
    /// ordinary operation may use, and <c>NeedsAttention</c>) refuses the single-machine read in the
    /// same breath, a support session resolves no Nayax credentials at all (issue #551), a
    /// cancellation is the caller's own, and a transport or <c>5xx</c> failure is an outage. Retrying
    /// any of those against another endpoint would spend a second request to reach the same answer
    /// slower, or would report a working site where there is an outage.</para>
    /// </summary>
    private async Task<long?> ResolveSiteAsync(long machineId, CancellationToken cancellationToken)
    {
        List<NayaxMachine> fleet;
        try
        {
            fleet = await _nayax.GetMachinesAsync(cancellationToken);
        }
        catch (NayaxPermissionNotGrantedException)
        {
            // Nayax has not granted this account the fleet list. The listing's own reads may well be
            // granted, and they are what it was made of before issue #846, so it is served exactly
            // as it was rather than refused. A refusal of the machine read too still propagates.
            return await ReadTheMachinesOwnSiteAsync(machineId, cancellationToken);
        }

        var listed = fleet.FirstOrDefault(machine => machine.MachineID == machineId);

        return listed is not null
            ? listed.CustomerID
            : await ReadTheMachinesOwnSiteAsync(machineId, cancellationToken);
    }

    /// <summary>
    /// The site on the machine's own Nayax record - the single-machine read this listing made before
    /// issue #846, unchanged, including that <c>GetMachineAsync</c> never returns null in practice
    /// and that a machine Nayax does not know fails as the existing controlled upstream failure.
    /// </summary>
    private async Task<long?> ReadTheMachinesOwnSiteAsync(long machineId, CancellationToken cancellationToken)
    {
        var machine = await _nayax.GetMachineAsync(machineId, cancellationToken);
        return machine?.CustomerID;
    }

    /// <summary>
    /// The catalogue product one machine mapping names, or <see langword="null"/> when it names no
    /// Nayax product, when this business holds no link for the one it names, or when the linked
    /// product is outside the catalogue this request read.
    /// </summary>
    private static ProductRecord? LocalProduct(
        long? nayaxProductId,
        IReadOnlyDictionary<long, long> localProductIdByNayaxId,
        IReadOnlyDictionary<long, ProductRecord> catalogueById) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? catalogueById.GetValueOrDefault(localProductId)
            : null;
}
