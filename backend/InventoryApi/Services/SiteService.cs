using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class SiteService : ISiteService
{
    private const decimal PaymentFee = 0.18m;
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;

    public SiteService(AppDbContext db, INayaxLynxClient nayaxLynxClient, IMachineService machineService)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
    }

    public async Task<List<SiteSummaryDto>> GetAll()
    {
        var machines = (await _nayaxLynxClient.GetMachinesAsync())
            .Where(machine => machine.CustomerID.HasValue)
            .GroupBy(machine => machine.CustomerID!.Value)
            .ToList();

        var products = await _db.Products.AsNoTracking().ToDictionaryAsync(product => product.Id);
        var summaries = await Task.WhenAll(machines.Select(site => BuildSummary(site.Key, site.ToList(), products)));

        return summaries.OrderBy(site => site.SiteName).ToList();
    }

    public async Task<List<SiteProductDto>> GetProducts(long siteId)
    {
        var machines = (await _nayaxLynxClient.GetMachinesAsync())
            .Where(machine => machine.CustomerID == siteId)
            .ToList();

        if (machines.Count == 0) return [];

        var products = await _db.Products.AsNoTracking().ToDictionaryAsync(product => product.Id);
        var machineProducts = await Task.WhenAll(
            machines.Select(machine => _nayaxLynxClient.GetMachineProductsAsync(machine.MachineID)));

        return AggregateProducts(machineProducts.SelectMany(items => items), products);
    }

    private async Task<SiteSummaryDto> BuildSummary(
        long siteId,
        List<NayaxMachine> machines,
        Dictionary<long, Models.Product> products)
    {
        var machineProductsTask = Task.WhenAll(
            machines.Select(machine => _nayaxLynxClient.GetMachineProductsAsync(machine.MachineID)));
        var salesTask = Task.WhenAll(
            machines.Select(machine => _db.NayaxSales.Where(s => s.MachineID == machine.MachineID && s.MachineAuthorizationTime > DateTime.Now.AddDays(-16)).ToListAsync()));

        await Task.WhenAll(machineProductsTask, salesTask);

        var machineProducts = (await machineProductsTask).SelectMany(items => items).ToList();
        var sales = (await salesTask).SelectMany(items => items).ToList();
        var now = DateTime.Now;
        var today = now.Date;
        var currentWeek = MachineService.GetWeekToDateRange(now);
        var previousComparableWeek = MachineService.GetPreviousComparableWeekRange(now);
        var stock = GetStockTotals(machineProducts);
        var stockCounts = GetStockCounts(machineProducts, products);

        return new SiteSummaryDto(
            siteId,
            GetSiteName(machines),
            machines.Count,
            stock.MaxStock == 0 ? 100 : (decimal)stock.QuantityInStock / stock.MaxStock * 100,
            stockCounts.LowProductCount,
            stockCounts.EmptyProductCount,
            sales.Where(sale => sale.MachineAuthorizationTime >= today &&
                                sale.MachineAuthorizationTime <= now)
                .Sum(sale => sale.SettlementValue),
            sales.Where(sale => sale.MachineAuthorizationTime >= currentWeek.Start &&
                                sale.MachineAuthorizationTime <= currentWeek.End)
                .Sum(sale => sale.SettlementValue),
            sales.Where(sale => sale.MachineAuthorizationTime >= previousComparableWeek.Start &&
                                sale.MachineAuthorizationTime <= previousComparableWeek.End)
                .Sum(sale => sale.SettlementValue));
    }

    private static (int LowProductCount, int EmptyProductCount) GetStockCounts(
        IEnumerable<NayaxMachineProduct> machineProducts,
        Dictionary<long, Models.Product> products)
    {
        var stockByProduct = machineProducts
            .Where(machineProduct => machineProduct.NayaxProductID.HasValue &&
                                     products.TryGetValue(machineProduct.NayaxProductID.Value, out var product) &&
                                     product.IsActive)
            .GroupBy(machineProduct => machineProduct.NayaxProductID!.Value)
            .Select(group => new
            {
                Quantity = group.Sum(machineProduct =>
                    (machineProduct.PAR ?? 0) - (machineProduct.MissingStockByMDB ?? 0)),
                Threshold = group.Sum(machineProduct => machineProduct.VendOutAlertThreshold ?? 0)
            });

        return (
            stockByProduct.Count(item => item.Quantity > 0 && item.Quantity <= item.Threshold),
            stockByProduct.Count(item => item.Quantity <= 0));
    }

    private static List<SiteProductDto> AggregateProducts(
        IEnumerable<NayaxMachineProduct> machineProducts,
        Dictionary<long, Models.Product> products)
    {
        var grouped = machineProducts
            .Where(machineProduct => machineProduct.NayaxProductID.HasValue &&
                                     products.ContainsKey(machineProduct.NayaxProductID.Value))
            .GroupBy(machineProduct => machineProduct.NayaxProductID!.Value);

        return grouped
            .Select(group =>
            {
                var product = products[group.Key];
                var items = group.ToList();
                var pricedItems = items.Where(item => item.RetailPrice.HasValue).ToList();
                var sitePrice = pricedItems.Count == 0
                    ? 0
                    : pricedItems.Average(item => item.RetailPrice!.Value);
                var profit = pricedItems.Count == 0
                    ? 0
                    : pricedItems.Average(item =>
                    {
                        var commission = item.CommissionValue ?? 0;
                        return item.RetailPrice!.Value -
                               (item.RetailPrice.Value * commission / 100) -
                               product.UnitPrice -
                               PaymentFee;
                    });

                return new SiteProductDto(
                    product.Id,
                    product.Name,
                    product.UnitPrice,
                    sitePrice,
                    profit,
                    items.Sum(item => (item.PAR ?? 0) - (item.MissingStockByMDB ?? 0)),
                    items.Sum(item => item.PAR ?? 0));
            })
            .OrderBy(product => product.Name)
            .ToList();
    }

    private static (int QuantityInStock, int MaxStock) GetStockTotals(
        IEnumerable<NayaxMachineProduct> machineProducts)
    {
        return machineProducts.Aggregate(
            (QuantityInStock: 0, MaxStock: 0),
            (totals, product) => (
                totals.QuantityInStock + (product.PAR ?? 0) - (product.MissingStockByMDB ?? 0),
                totals.MaxStock + (product.PAR ?? 0)));
    }

    private static string GetSiteName(IEnumerable<NayaxMachine> machines)
    {
        var machineName = machines
            .Select(machine => machine.MachineName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        return machineName?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Unnamed site";
    }
}
