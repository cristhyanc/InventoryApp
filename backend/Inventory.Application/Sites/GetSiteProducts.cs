using Inventory.Application.Nayax;
using Inventory.Application.Time;
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
    private readonly IBusinessCalendar _businessCalendar;

    public GetSiteProducts(INayaxLynxClient nayax, ISiteFactsStore facts, IBusinessCalendar businessCalendar)
    {
        _nayax = nayax;
        _facts = facts;
        _businessCalendar = businessCalendar;
    }

    public async Task<IReadOnlyList<SiteProductRecord>> Handle(long siteId, CancellationToken cancellationToken)
    {
        var machines = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID == siteId)
            .ToList();
        if (machines.Count == 0) return [];

        // Paired with the machine the request was actually made for, not re-derived from the
        // response's own MachineID field: GET .../machineProducts documents MachineID as nullable,
        // and CalculateReorderNeeds/GetPickList already avoid trusting it for the same reason.
        var machineProductsByMachine = await Task.WhenAll(
            machines.Select(async machine => (
                Machine: machine,
                Products: (await _nayax.GetMachineProductsAsync(machine.MachineID, cancellationToken))
                    .Where(mp => mp.NayaxProductID.HasValue)
                    .ToList())));

        var machineProducts = machineProductsByMachine.SelectMany(entry => entry.Products).ToList();

        // The effective-dated commission and Nayax fee configuration is selected by the Australia/Sydney
        // business date (issue #310), not the host's local date, exactly as ResolveMachineProductPricing
        // selects it for the machine product listing that shares this port.
        var today = _businessCalendar.Today;
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

        var machineMdbCodesByProduct = machineProductsByMachine
            .SelectMany(entry => entry.Products.Select(mp => (entry.Machine, Product: mp)))
            .GroupBy(pair => pair.Product.NayaxProductID!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SiteProductMachineMdbCode>)group
                    .OrderBy(pair => pair.Machine.MachineID)
                    .Select(pair => new SiteProductMachineMdbCode(
                        pair.Machine.MachineID, MachineLabel(pair.Machine), pair.Product.MDBCode))
                    .ToList());

        return SiteProductPricingPolicy
            .Calculate(priceFacts, costBasis, feeExGst, commission.ConfigurationUnavailable)
            .Select(result =>
            {
                var machineMdbCodes = machineMdbCodesByProduct.GetValueOrDefault(
                    result.ProductId, Array.Empty<SiteProductMachineMdbCode>());
                var mdbCode = machineMdbCodes.Select(code => code.MdbCode).Min();

                return new SiteProductRecord(
                    result.ProductId, result.Name, result.AverageUnitCost, result.SitePrice,
                    result.EstimatedCardProfit, result.QuantityInStock, result.MaxStock,
                    mdbCode, machineMdbCodes);
            })
            .ToList();
    }

    /// <summary>
    /// The display label for one of the site's machines in the MDB Code cell, matching the
    /// name/number/id fallback <c>PickListComponent.machineLabel</c> already applies on the frontend
    /// (issue #222) when no machine name is configured in Nayax.
    /// </summary>
    private static string MachineLabel(NayaxMachine machine)
    {
        if (!string.IsNullOrWhiteSpace(machine.MachineName)) return machine.MachineName;
        if (!string.IsNullOrWhiteSpace(machine.MachineNumber)) return machine.MachineNumber;
        return $"Machine #{machine.MachineID}";
    }
}
