using Inventory.Application.Nayax;
using Inventory.Application.Products;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine product listing use case: matches the machine's live Nayax product mappings against
/// the local catalogue, overlays the machine-specific price/commission/MDB/stock facts, and resolves
/// each row's suggested net/price values through <see cref="ResolveMachineProductPricing"/>. Mirrors
/// the former <c>InventoryApi.Services.MachineService.GetMachineProducts</c> exactly (issue #240): the
/// same two Nayax calls in the same order with the same arguments, a Nayax mapping with no matching
/// local product is dropped, and the result is ordered by MDB code.
/// </summary>
public sealed class ListMachineProducts
{
    private readonly INayaxLynxClient _nayax;
    private readonly IProductCatalogStore _catalog;
    private readonly ResolveMachineProductPricing _resolvePricing;

    public ListMachineProducts(
        INayaxLynxClient nayax, IProductCatalogStore catalog, ResolveMachineProductPricing resolvePricing)
    {
        _nayax = nayax;
        _catalog = catalog;
        _resolvePricing = resolvePricing;
    }

    public async Task<IReadOnlyList<MachineProductRecord>> Handle(long machineId, CancellationToken cancellationToken)
    {
        var machine = await _nayax.GetMachineAsync(machineId, cancellationToken);
        var nayaxMachineProducts = await _nayax.GetMachineProductsAsync(machineId, cancellationToken);
        var catalogue = await _catalog.ListUnorderedAsync(ProductCatalogFilter.None, cancellationToken);

        var rows = nayaxMachineProducts
            .Select(machineProduct =>
            {
                var source = catalogue.FirstOrDefault(product => product.Id == machineProduct.NayaxProductID);
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
        var pricingResults = await _resolvePricing.Handle(machine?.CustomerID, pricingFacts, cancellationToken);

        return rows
            .Select((row, index) => row with
            {
                SuggestedNetValue = pricingResults[index].SuggestedNetValue,
                SuggestedPriceValue = pricingResults[index].SuggestedPriceValue,
            })
            .OrderBy(row => row.MdbCode)
            .ToList();
    }
}
