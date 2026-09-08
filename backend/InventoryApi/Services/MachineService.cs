using ClosedXML.Excel;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Reflection;

namespace InventoryApi.Services;

public class MachineService : IMachineService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly ISaleCostingService _saleCosting;
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;
    private readonly IInventoryCostRebuildService _inventoryCostRebuild;

    public MachineService(AppDbContext db, INayaxLynxClient nayaxLynxClient, ISaleCostingService? saleCosting = null, INayaxProcessingFeeService? nayaxProcessingFees = null, IInventoryCostRebuildService? inventoryCostRebuild = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _saleCosting = saleCosting ?? new SaleCostingService(db);
        _nayaxProcessingFees = nayaxProcessingFees ?? new NayaxProcessingFeeService(db);
        _inventoryCostRebuild = inventoryCostRebuild ?? new InventoryCostRebuildService(db);
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
        await SaveMachinesLastSalesAsync(nayaxMachines.Select(x => x.MachineID).ToList());
        var products = await _db.Products.ToListAsync();
        var machines = new List<Machine>();
        foreach (var machine in nayaxMachines)
            machines.Add(await GetMachineSalesAsync(machine, products));
        return machines;
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
        var now = DateTime.Now;
        var today = now.Date;
        var currentWeek = GetWeekToDateRange(now);
        var previousComparableWeek = GetPreviousComparableWeekRange(now);
        var lastWeek = GetWeekRange(today, -1);
        var monthToDate = GetMonthToDateRange(now);
        var twoWeeksAgo = GetWeekRange(today, -2);

        var lastSalesTask = _db.NayaxSales.Where(s => s.MachineID == machine.MachineID && s.MachineAuthorizationTime > DateTime.Now.AddMonths(-1)).ToListAsync();
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

        var todaySales = lastSales.Where(s => s.MachineAuthorizationTime >= today &&
                                              s.MachineAuthorizationTime <= now && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var currentWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= currentWeek.Start && s.MachineAuthorizationTime <= currentWeek.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var previousComparableWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= previousComparableWeek.Start && s.MachineAuthorizationTime <= previousComparableWeek.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var lastWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= lastWeek.Start && s.MachineAuthorizationTime <= lastWeek.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var monthToDateSales = lastSales.Where(s => s.MachineAuthorizationTime >= monthToDate.Start && s.MachineAuthorizationTime <= monthToDate.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var twoWeeksAgoSales = lastSales.Where(s => s.MachineAuthorizationTime >= twoWeeksAgo.Start && s.MachineAuthorizationTime <= twoWeeksAgo.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();

        results.CurrentWeekNetRevenue = await CalculateRevenueAsync(currentWeekSales, machineProducts, products, currentWeek.Start, currentWeek.End, machine.MachineID);
        results.PreviousComparableWeekNetRevenue = await CalculateRevenueAsync(previousComparableWeekSales, machineProducts, products, previousComparableWeek.Start, previousComparableWeek.End, machine.MachineID);
        results.LastWeekNetRevenue = await CalculateRevenueAsync(lastWeekSales, machineProducts, products, lastWeek.Start, lastWeek.End, machine.MachineID);
        results.TodayNetRevenue = await CalculateRevenueAsync(todaySales, machineProducts, products, today, now, machine.MachineID);
        results.MonthToDateNetRevenue = await CalculateRevenueAsync(monthToDateSales, machineProducts, products, monthToDate.Start, monthToDate.End, machine.MachineID);
        results.TwoWeeksAgoNetRevenue = await CalculateRevenueAsync(twoWeeksAgoSales, machineProducts, products, twoWeeksAgo.Start, twoWeeksAgo.End, machine.MachineID);

        results.TodayGrossRevenue = todaySales.Sum(s => s.SettlementValue);
        results.CurrentWeekGrossRevenue = currentWeekSales.Sum(s => s.SettlementValue);
        results.PreviousComparableWeekGrossRevenue = previousComparableWeekSales.Sum(s => s.SettlementValue);
        results.LastWeekGrossRevenue = lastWeekSales.Sum(s => s.SettlementValue);
        results.MonthToDateGrossRevenue = monthToDateSales.Sum(s => s.SettlementValue);
        results.TwoWeeksAgoGrossRevenue = twoWeeksAgoSales.Sum(s => s.SettlementValue);

        return results;
    }

    private async Task SaveMachinesLastSalesAsync(List<long> machineIds, CancellationToken ct = default)
    {
        var products = await _db.Products.AsNoTracking().ToListAsync(ct);
        var affected = new Dictionary<long, DateTime>();
        foreach (var machineId in machineIds)
        {
            var sales = await _nayaxLynxClient.GetMachineLastSalesAsync(machineId, ct);
            foreach (var sale in sales)
            {
                var existing = await _db.NayaxSales
                    .FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, ct);
                if (existing is null)
                {
                    var added = new NayaxSales
                    {
                        TransactionID = sale.TransactionID,
                        TransactionStatusId = sale.TransactionStatusId,
                        MachineID = sale.MachineID,
                        NayaxProductId = sale.NayaxProductId,
                        MachineName = sale.MachineName,
                        SettlementValue = sale.SettlementValue,
                        PaymentMethod = sale.PaymentMethod,
                        ProductName = sale.ProductName,
                        NayaxProductCostPrice = sale.ProductCostPrice,
                        MachineAuthorizationTime = sale.MachineAuthorizationTime
                    };
                    _db.NayaxSales.Add(added);
                    await _saleCosting.CostSaleAsync(added, allowLegacyEstimate: true, cancellationToken: ct);
                    if (NayaxTransactionStatusClassifier.IsCompletedSale(added))
                    {
                        var product = NayaxProductMatcher.Match(products, added.NayaxProductId, added.ProductName);
                        if (product is not null &&
                            (!affected.TryGetValue(product.Id, out var existingAt) || added.MachineAuthorizationTime < existingAt))
                            affected[product.Id] = added.MachineAuthorizationTime;
                    }
                    continue;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        if (affected.Count == 0)
            return;
        var baselines = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => affected.Keys.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, ct);
        foreach (var item in affected)
            if (baselines.TryGetValue(item.Key, out var cutoff) && item.Value > cutoff)
                await _inventoryCostRebuild.RebuildAsync(item.Key, item.Value, cancellationToken: ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<decimal> CalculateRevenueAsync(List<NayaxSales> sales, List<NayaxMachineProduct> machineProducts, List<Product> products, DateTime from, DateTime to, long machineId)
    {
        decimal totalRevenue = 0;
        decimal machineCommission = machineProducts
            .FirstOrDefault(x => x.CommissionValue.HasValue && x.CommissionValue.Value > 0)
            ?.CommissionValue ?? 0m;

        foreach (var sale in sales)
        {
            var product = NayaxProductMatcher.Match(products, sale.NayaxProductId, sale.ProductName);

            if (product != null)
            {
                decimal productCost = sale.CostOfGoodsSold ?? 0m;
                decimal commission = machineCommission != 0 ? sale.SettlementValue * machineCommission / 100 : 0;
                totalRevenue += sale.SettlementValue - productCost - commission;
            }
        }
        var fees = await _nayaxProcessingFees.GetProcessingFeesAsync(from, to, machineId);
        return totalRevenue - fees.TotalFeeIncGst;
    }

    public static (DateTime Start, DateTime End) GetWeekRange(DateTime referenceDate, int weeksOffset = 0)
    {
        DateTime date = referenceDate.Date.AddDays(weeksOffset * 7);
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        DateTime startOfWeek = date.AddDays(-diff);
        DateTime endOfWeek = startOfWeek.AddDays(7).AddMilliseconds(-1);
        return (startOfWeek, endOfWeek);
    }

    public static (DateTime Start, DateTime End) GetWeekToDateRange(DateTime referenceDate)
    {
        var start = GetWeekRange(referenceDate.Date).Start;
        return (start, referenceDate);
    }

    public static (DateTime Start, DateTime End) GetPreviousComparableWeekRange(DateTime referenceDate)
    {
        var current = GetWeekToDateRange(referenceDate);
        return (current.Start.AddDays(-7), current.End.AddDays(-7));
    }

    public static (DateTime Start, DateTime End) GetMonthToDateRange(DateTime referenceDate)
    {
        return (new DateTime(referenceDate.Year, referenceDate.Month, 1), referenceDate);
    }
}
