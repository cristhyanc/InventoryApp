using Inventory.Application.Nayax;
using Inventory.Domain.Sites;

namespace Inventory.Application.Sites;

/// <summary>
/// The site product preview use case: for every machine at a site, aggregates its Nayax product
/// mappings into one row per catalogue product with an estimated card-sale profit. Mirrors the former
/// <c>InventoryApi.Services.SiteService.GetProducts</c> exactly (issue #241).
/// </summary>
public sealed class GetSiteProducts
{
    private readonly INayaxLynxClient _nayax;
    private readonly ISiteFactsStore _facts;

    public GetSiteProducts(INayaxLynxClient nayax, ISiteFactsStore facts)
    {
        _nayax = nayax;
        _facts = facts;
    }

    public async Task<IReadOnlyList<SiteProductRecord>> Handle(long siteId, CancellationToken cancellationToken)
    {
        var machines = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID == siteId)
            .ToList();
        if (machines.Count == 0) return [];

        var machineProducts = (await Task.WhenAll(
                machines.Select(machine => _nayax.GetMachineProductsAsync(machine.MachineID, cancellationToken))))
            .SelectMany(items => items)
            .Where(mp => mp.NayaxProductID.HasValue)
            .ToList();

        var today = DateTime.Today;
        var distinctPrices = machineProducts
            .Where(mp => mp.RetailPrice.HasValue)
            .Select(mp => mp.RetailPrice!.Value)
            .Distinct()
            .ToList();

        // Awaited one at a time, deliberately not with Task.WhenAll: all three reads land on the same
        // scoped ISiteFactsStore, whose EF adapter shares one AppDbContext, and a DbContext supports
        // only one operation at a time. SQLite's synchronous async implementation would usually hide
        // the overlap locally while failing against a real asynchronous provider. The Nayax requests
        // above stay a concurrent fan-out: they are independent remote reads that touch no DbContext.
        var costBasis = await _facts.GetProductCostBasisAsync(cancellationToken);
        var commission = await _facts.ResolveCardCommissionAsync(siteId, today, distinctPrices, cancellationToken);
        var feeExGst = await _facts.ResolveEffectiveFeeExGstAsync(today, cancellationToken);

        var priceFacts = machineProducts.Select(mp => new SiteProductPriceFact(
            mp.NayaxProductID!.Value,
            mp.RetailPrice,
            mp.RetailPrice.HasValue ? commission.CommissionAmountByRetailPrice.GetValueOrDefault(mp.RetailPrice.Value) : 0m,
            mp.PAR ?? 0,
            mp.MissingStockByMDB ?? 0));

        return SiteProductPricingPolicy
            .Calculate(priceFacts, costBasis, feeExGst, commission.ConfigurationUnavailable)
            .Select(result => new SiteProductRecord(
                result.ProductId, result.Name, result.AverageUnitCost, result.SitePrice,
                result.EstimatedCardProfit, result.QuantityInStock, result.MaxStock))
            .ToList();
    }
}
