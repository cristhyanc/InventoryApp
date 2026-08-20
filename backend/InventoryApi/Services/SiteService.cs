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

    public SiteService(AppDbContext db, INayaxLynxClient nayaxLynxClient)
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
            machines.Select(machine => _nayaxLynxClient.GetMachineLastSalesAsync(machine.MachineID)));

        await Task.WhenAll(machineProductsTask, salesTask);

        var machineProducts = (await machineProductsTask).SelectMany(items => items).ToList();
        var sales = (await salesTask).SelectMany(items => items).ToList();
        var today = DateTime.Today;
        var currentWeek = MachineService.GetWeekRange(today);
        var lastWeek = MachineService.GetWeekRange(today, -1);
        var stock = GetStockTotals(machineProducts);

        return new SiteSummaryDto(
            siteId,
            GetSiteName(machines),
            machines.Count,
            stock.MaxStock == 0 ? 100 : (decimal)stock.QuantityInStock / stock.MaxStock * 100,
            GetEmptyProductCount(machineProducts, products),
            sales.Where(sale => sale.MachineAuthorizationTime >= today).Sum(sale => sale.SettlementValue),
            sales.Where(sale => sale.MachineAuthorizationTime >= currentWeek.Start &&
                                sale.MachineAuthorizationTime <= currentWeek.End)
                .Sum(sale => sale.SettlementValue),
            sales.Where(sale => sale.MachineAuthorizationTime >= lastWeek.Start &&
                                sale.MachineAuthorizationTime <= lastWeek.End)
                .Sum(sale => sale.SettlementValue));
    }

    private static int GetEmptyProductCount(
        IEnumerable<NayaxMachineProduct> machineProducts,
        Dictionary<long, Models.Product> products)
    {
        return machineProducts
            .Where(machineProduct => machineProduct.NayaxProductID.HasValue &&
                                     products.ContainsKey(machineProduct.NayaxProductID.Value))
            .GroupBy(machineProduct => machineProduct.NayaxProductID!.Value)
            .Count(group => group.Sum(machineProduct =>
                (machineProduct.PAR ?? 0) - (machineProduct.MissingStockByMDB ?? 0)) <= 0);
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
