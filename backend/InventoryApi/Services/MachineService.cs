using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class MachineService : IMachineService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;

    public MachineService(AppDbContext db, INayaxLynxClient nayaxLynxClient)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
    }

    public async Task<Machine?> GetById(long id)
    {
        var nayaxMachine = await _nayaxLynxClient.GetMachineAsync(id);
        if (nayaxMachine == null) return null;
        var products = await _db.Products.ToListAsync();
        return await GetMachineSalesAsync(nayaxMachine, products);
    }

    public async Task<List<Machine>> GetAll()
    {
        var nayaxMachines = await _nayaxLynxClient.GetMachinesAsync();
        var products = await _db.Products.ToListAsync();
        var machines = await Task.WhenAll(nayaxMachines.Select(x => GetMachineSalesAsync(x, products)));
        return machines.ToList();
    }

    public async Task<List<Product>> GetMachineProducts(long id)
    {
        var nayaxMachineProducts = await _nayaxLynxClient.GetMachineProductsAsync(id);
        var productList = await _db.Products.Include(x => x.Category).AsNoTracking().ToListAsync();
        var products = nayaxMachineProducts.Select(mp =>
        {
            var result = productList.First(p => p.Id == mp.NayaxProductID).Clone();
            result.MachinePrice = mp.RetailPrice ?? 0;
            result.CommissionValue = mp.CommissionValue ?? 0;
            result.SuggestedNetValue = result.MachinePrice - (result.MachinePrice * result.CommissionValue / 100) - result.UnitPrice - (decimal)0.18;
            result.SuggestedPriceValue = ((decimal)0.18 + result.UnitPrice) / ((decimal)0.5 - (result.CommissionValue / 100));
            result.MdbCode = mp.MDBCode;
            result.QuantityInStock = (mp.PAR - mp.MissingStockByMDB) ?? 0;
            result.MaxStockInMachine = mp.PAR;
            return result;
        }).ToList().OrderBy(x => x.MdbCode).ToList();

        return products;
    }

    private async Task<Machine> GetMachineSalesAsync(NayaxMachine machine, List<Product> products)
    {
        var today = DateTime.Today;
        var currentWeek = GetWeekRange(today, 0);
        var lastWeek = GetWeekRange(today, -1);
        var twoWeeksAgo = GetWeekRange(today, -2);

        var lastSalesTask = _nayaxLynxClient.GetMachineLastSalesAsync(machine.MachineID);
        var machineProductsTask = _nayaxLynxClient.GetMachineProductsAsync(machine.MachineID);

        await Task.WhenAll(lastSalesTask, machineProductsTask);

        var lastSales = await lastSalesTask;
        var machineProducts = await machineProductsTask;

        var results = new Machine
        {
            ActorID = machine.ActorID,
            MachineID = machine.MachineID,
            MachineName = machine.MachineName,
            MachineNumber = machine.MachineNumber
        };

        var todaySales = lastSales.Where(s => s.MachineAuthorizationTime >= today).ToList();
        var currentWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= currentWeek.Start && s.MachineAuthorizationTime <= currentWeek.End).ToList();
        var lastWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= lastWeek.Start && s.MachineAuthorizationTime <= lastWeek.End).ToList();
        var twoWeeksAgoSales = lastSales.Where(s => s.MachineAuthorizationTime >= twoWeeksAgo.Start && s.MachineAuthorizationTime <= twoWeeksAgo.End).ToList();

        results.CurrentWeekNetRevenue = CalculateRevenue(currentWeekSales, machineProducts, products);
        results.LastWeekNetRevenue = CalculateRevenue(lastWeekSales, machineProducts, products);
        results.TodayNetRevenue = CalculateRevenue(todaySales, machineProducts, products);
        results.TwoWeeksAgoGrossRevenue = CalculateRevenue(twoWeeksAgoSales, machineProducts, products);

        results.TodayGrossRevenue = todaySales.Sum(s => s.SettlementValue);
        results.CurrentWeekGrossRevenue = currentWeekSales.Sum(s => s.SettlementValue);
        results.LastWeekGrossRevenue = lastWeekSales.Sum(s => s.SettlementValue);
        results.TwoWeeksAgoGrossRevenue = twoWeeksAgoSales.Sum(s => s.SettlementValue);

        return results;
    }

    private static decimal CalculateRevenue(List<NayaxLastSalesReport> sales, List<NayaxMachineProduct> machineProducts, List<Product> products)
    {
        decimal totalRevenue = 0;
        decimal machineCommission = machineProducts
            .FirstOrDefault(x => x.CommissionValue.HasValue && x.CommissionValue.Value > 0)
            ?.CommissionValue ?? 0m;

        foreach (var sale in sales)
        {
            var salesName = sale.ProductName?.Split("(").First();
            var product = products.SingleOrDefault(p => p.Name == salesName);
            if (product != null)
            {
                decimal productCost = product?.UnitPrice ?? 0;
                decimal commission = machineCommission != 0 ? sale.SettlementValue * machineCommission / 100 : 0;
                decimal paymentFee = (decimal)(sale.PaymentMethod == "Cash" ? 0 : 0.18);
                totalRevenue += sale.SettlementValue - (productCost * (sale.Quantity == 0 ? 1 : sale.Quantity)) - commission - paymentFee;
            }
        }
        return totalRevenue;
    }

    public static (DateTime Start, DateTime End) GetWeekRange(DateTime referenceDate, int weeksOffset = 0)
    {
        DateTime date = referenceDate.Date.AddDays(weeksOffset * 7);
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        DateTime startOfWeek = date.AddDays(-diff);
        DateTime endOfWeek = startOfWeek.AddDays(7).AddMilliseconds(-1);
        return (startOfWeek, endOfWeek);
    }
}
