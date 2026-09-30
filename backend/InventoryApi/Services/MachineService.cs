using Inventory.Application.Machines;
using Inventory.Application.Products;
using InventoryApi.Data;
using Inventory.Application.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

/// <summary>
/// <see cref="GetById"/>/<see cref="GetAll"/> delegate to the migrated
/// <see cref="Inventory.Application.Machines.GetMachineDashboard"/>/<see cref="Inventory.Application.Machines.ListMachineDashboard"/>
/// use cases (issue #241), mapping their result to the <see cref="Machine"/> API contract unchanged.
/// <see cref="GetMachineProducts"/> still clones and returns the EF <see cref="Product"/> entity
/// directly (its full field set has no Application-owned equivalent - see <c>docs/architecture.md</c>
/// backend migration track item 6), but its suggested net/price commission-and-fee calculation now
/// delegates to <see cref="ResolveMachineProductPricing"/> (issue #240), reusing the site dashboard's
/// commission/fee resolution port instead of a third copy of that lookup.
/// </summary>
public class MachineService : IMachineService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly GetMachineDashboard _getMachineDashboard;
    private readonly ListMachineDashboard _listMachineDashboard;
    private readonly ResolveMachineProductPricing _resolveMachineProductPricing;

    public MachineService(
        AppDbContext db,
        INayaxLynxClient nayaxLynxClient,
        GetMachineDashboard getMachineDashboard,
        ListMachineDashboard listMachineDashboard,
        ResolveMachineProductPricing resolveMachineProductPricing)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _getMachineDashboard = getMachineDashboard;
        _listMachineDashboard = listMachineDashboard;
        _resolveMachineProductPricing = resolveMachineProductPricing;
    }

    public async Task<Machine?> GetById(long id)
    {
        var summary = await _getMachineDashboard.Handle(id, CancellationToken.None);
        return summary is null ? null : ToMachine(summary);
    }

    public async Task<List<Machine>> GetAll()
    {
        var summaries = await _listMachineDashboard.Handle(CancellationToken.None);
        return summaries.Select(ToMachine).ToList();
    }

    private static Machine ToMachine(MachineSummary summary) => new()
    {
        ActorID = summary.ActorId,
        MachineID = summary.MachineId,
        MachineName = summary.MachineName,
        MachineNumber = summary.MachineNumber,
        TodayGrossRevenue = summary.TodayGrossRevenue,
        CurrentWeekGrossRevenue = summary.CurrentWeekGrossRevenue,
        PreviousComparableWeekGrossRevenue = summary.PreviousComparableWeekGrossRevenue,
        LastWeekGrossRevenue = summary.LastWeekGrossRevenue,
        MonthToDateGrossRevenue = summary.MonthToDateGrossRevenue,
        TwoWeeksAgoGrossRevenue = summary.TwoWeeksAgoGrossRevenue,
        TodayDirectProfit = summary.TodayDirectProfit,
        CurrentWeekDirectProfit = summary.CurrentWeekDirectProfit,
        PreviousComparableWeekDirectProfit = summary.PreviousComparableWeekDirectProfit,
        LastWeekDirectProfit = summary.LastWeekDirectProfit,
        MonthToDateDirectProfit = summary.MonthToDateDirectProfit,
        TwoWeeksAgoDirectProfit = summary.TwoWeeksAgoDirectProfit,
        ProfitabilityStatus = summary.ProfitabilityStatus,
    };

    public async Task<List<Product>> GetMachineProducts(long id)
    {
        var machine = await _nayaxLynxClient.GetMachineAsync(id);
        var nayaxMachineProducts = await _nayaxLynxClient.GetMachineProductsAsync(id);
        var productList = await _db.Products
            .Include(x => x.Category)
            .Include(x => x.Supplier)
            .Include(x => x.StockAdjustments)
            .AsNoTracking()
            .ToListAsync();

        var products = nayaxMachineProducts.Select(mp =>
        {
            var source = productList.FirstOrDefault(p => p.Id == mp.NayaxProductID);
            if (source is null) return null;
            var result = source.Clone();
            result.MachinePrice = mp.RetailPrice ?? 0;
            result.CommissionValue = mp.CommissionValue ?? 0;
            result.MdbCode = mp.MDBCode;
            result.QuantityInStock = (mp.PAR - mp.MissingStockByMDB) ?? 0;
            result.MaxStockInMachine = mp.PAR;
            return result;
        }).Where(x => x is not null).Cast<Product>().ToList();

        // Positional, not keyed by product id: more than one machine slot can list the same
        // product with a different machine price, so a dictionary keyed by id would collide.
        var pricingFacts = products
            .Select(product => new MachineProductPricingFact(product.Id, product.MachinePrice, product.AverageUnitCost))
            .ToList();
        var pricingResults = await _resolveMachineProductPricing.Handle(machine?.CustomerID, pricingFacts, CancellationToken.None);
        for (var index = 0; index < products.Count; index++)
        {
            products[index].SuggestedNetValue = pricingResults[index].SuggestedNetValue;
            products[index].SuggestedPriceValue = pricingResults[index].SuggestedPriceValue;
        }

        return products.OrderBy(x => x.MdbCode).ToList();
    }
}
