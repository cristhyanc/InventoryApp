using Inventory.Domain.Reporting;
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
        return await GetMachineSalesAsync(nayaxMachine);
    }

    public async Task<List<Machine>> GetAll()
    {
        var nayaxMachines = await _nayaxLynxClient.GetMachinesAsync();
        await SaveMachinesLastSalesAsync(nayaxMachines.Select(x => x.MachineID).ToList());
        var machines = new List<Machine>();
        foreach (var machine in nayaxMachines)
            machines.Add(await GetMachineSalesAsync(machine));
        return machines;
    }

    public async Task<List<Product>> GetMachineProducts(long id)
    {
        var machine = await _nayaxLynxClient.GetMachineAsync(id);
        var nayaxMachineProducts = await _nayaxLynxClient.GetMachineProductsAsync(id);
        var productList = await _db.Products.Include(x => x.Category).AsNoTracking().ToListAsync();
        var today = DateTime.Today;
        var agreements = machine?.CustomerID is long siteId
            ? await _db.SiteCommissionAgreements.AsNoTracking()
                .Where(x => x.SiteId == siteId)
                .ToListAsync()
            : [];
        var rates = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .Where(x => x.EffectiveFrom <= today)
            .OrderBy(x => x.EffectiveFrom)
            .ToListAsync();
        SiteCommissionAgreement? agreement = null;
        var commissionConfigurationUnavailable = false;
        if (machine?.CustomerID is long currentSiteId)
        {
            try
            {
                agreement = EffectiveFinancialConfiguration.ResolveAgreement(agreements, currentSiteId, today);
                commissionConfigurationUnavailable = agreement is null && agreements.Count > 0;
            }
            catch (InvalidOperationException)
            {
                commissionConfigurationUnavailable = true;
            }
        }
        var feeRate = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, today);
        var feeIncGst = feeRate is null ? 0m : feeRate.FeeExGst + ReportingCalculations.GstFromExcluding(feeRate.FeeExGst);

        var products = nayaxMachineProducts.Select(mp =>
        {
            var source = productList.FirstOrDefault(p => p.Id == mp.NayaxProductID);
            if (source is null) return null;
            var result = source.Clone();
            result.MachinePrice = mp.RetailPrice ?? 0;
            result.CommissionValue = mp.CommissionValue ?? 0;
            var hasCostBasis = result.AverageUnitCost > 0m;
            if (machine?.CustomerID.HasValue == true && !commissionConfigurationUnavailable && feeRate is not null && hasCostBasis)
            {
                var commission = agreement is null
                    ? 0m
                    : SiteCommissionCalculator.CommissionAmount(
                        agreement, result.MachinePrice, NayaxPaymentType.Card);
                result.SuggestedNetValue =
                    result.MachinePrice - result.AverageUnitCost - commission - feeIncGst;

                var commissionPerDollar = agreement is null
                    ? 0m
                    : SiteCommissionCalculator.CommissionAmount(
                        agreement, 1m, NayaxPaymentType.Card);
                var denominator = 0.5m - commissionPerDollar;
                result.SuggestedPriceValue = denominator > 0m
                    ? (result.AverageUnitCost + feeIncGst) / denominator
                    : null;
            }
            result.MdbCode = mp.MDBCode;
            result.QuantityInStock = (mp.PAR - mp.MissingStockByMDB) ?? 0;
            result.MaxStockInMachine = mp.PAR;
            return result;
        }).Where(x => x is not null).Cast<Product>().OrderBy(x => x.MdbCode).ToList();

        return products;
    }

    private async Task<Machine> GetMachineSalesAsync(NayaxMachine machine)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var currentWeek = GetWeekToDateRange(now);
        var previousComparableWeek = GetPreviousComparableWeekRange(now);
        var lastWeek = GetWeekRange(today, -1);
        var monthToDate = GetMonthToDateRange(now);
        var twoWeeksAgo = GetWeekRange(today, -2);

        var lastSalesTask = _db.NayaxSales.Where(s => s.MachineID == machine.MachineID && s.MachineAuthorizationTime > DateTime.Now.AddMonths(-1)).ToListAsync();
        var lastSales = await lastSalesTask;
        var agreementFrom = lastSales.Count == 0 ? today : lastSales.Min(x => x.MachineAuthorizationTime.Date);
        var agreements = machine.CustomerID.HasValue
            ? await _db.SiteCommissionAgreements.AsNoTracking()
                .Where(x => x.SiteId == machine.CustomerID.Value &&
                    x.EffectiveFrom <= now &&
                    (x.EffectiveTo == null || x.EffectiveTo >= agreementFrom))
                .OrderBy(x => x.EffectiveFrom)
                .ToListAsync()
            : [];

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

        results.CurrentWeekDirectProfit = await CalculateDirectProfitAsync(currentWeekSales, agreements, machine.CustomerID, currentWeek.Start, currentWeek.End, machine.MachineID);
        results.PreviousComparableWeekDirectProfit = await CalculateDirectProfitAsync(previousComparableWeekSales, agreements, machine.CustomerID, previousComparableWeek.Start, previousComparableWeek.End, machine.MachineID);
        results.LastWeekDirectProfit = await CalculateDirectProfitAsync(lastWeekSales, agreements, machine.CustomerID, lastWeek.Start, lastWeek.End, machine.MachineID);
        results.TodayDirectProfit = await CalculateDirectProfitAsync(todaySales, agreements, machine.CustomerID, today, now, machine.MachineID);
        results.MonthToDateDirectProfit = await CalculateDirectProfitAsync(monthToDateSales, agreements, machine.CustomerID, monthToDate.Start, monthToDate.End, machine.MachineID);
        results.TwoWeeksAgoDirectProfit = await CalculateDirectProfitAsync(twoWeeksAgoSales, agreements, machine.CustomerID, twoWeeksAgo.Start, twoWeeksAgo.End, machine.MachineID);
        var completedSales = lastSales.Where(NayaxTransactionStatusClassifier.IsCompletedSale).ToList();
        results.ProfitabilityStatus = GetProfitabilityStatus(completedSales, agreements, machine.CustomerID);
        if (results.ProfitabilityStatus is null && completedSales.Count > 0 &&
            new[]
            {
                results.TodayDirectProfit,
                results.CurrentWeekDirectProfit,
                results.PreviousComparableWeekDirectProfit,
                results.LastWeekDirectProfit,
                results.MonthToDateDirectProfit,
                results.TwoWeeksAgoDirectProfit
            }.Any(x => !x.HasValue))
            results.ProfitabilityStatus = "Unavailable: an effective Nayax processing fee rate is missing.";

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
                var matchedProduct = NayaxProductMatcher.Match(
                    products,
                    sale.NayaxProductId,
                    sale.ProductName);
                var existing = await _db.NayaxSales
                    .FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, ct);
                if (existing is null)
                {
                    var added = new NayaxSales
                    {
                        TransactionID = sale.TransactionID,
                        TransactionStatusId = sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250,
                        MachineID = sale.MachineID,
                        NayaxProductId = matchedProduct?.Id ?? sale.NayaxProductId,
                        MachineName = sale.MachineName,
                        SettlementValue = sale.SettlementValue,
                        PaymentMethod = sale.PaymentMethod,
                        ProductName = sale.ProductName,
                        NayaxProductCostPrice = sale.ProductCostPrice,
                        MachineAuthorizationTime = sale.MachineAuthorizationTime
                    };
                    _db.NayaxSales.Add(added);
                    await _saleCosting.CostSaleAsync(added, cancellationToken: ct);
                    if (NayaxTransactionStatusClassifier.IsCompletedSale(added))
                    {
                        if (matchedProduct is not null &&
                            (!affected.TryGetValue(matchedProduct.Id, out var existingAt) || added.MachineAuthorizationTime < existingAt))
                            affected[matchedProduct.Id] = added.MachineAuthorizationTime;
                    }
                    continue;
                }

                var enriched = false;
                if (!existing.NayaxProductId.HasValue)
                {
                    var existingMatch = NayaxProductMatcher.Match(
                        products,
                        sale.NayaxProductId,
                        sale.ProductName ?? existing.ProductName);
                    if (existingMatch is not null)
                    {
                        existing.NayaxProductId = existingMatch.Id;
                        matchedProduct = existingMatch;
                        enriched = true;
                    }
                }
                if (!existing.TransactionStatusId.HasValue)
                {
                    existing.TransactionStatusId =
                        sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250;
                    enriched = true;
                }
                if (enriched)
                {
                    await _saleCosting.CostSaleAsync(
                        existing,
                        cancellationToken: ct);
                    if (NayaxTransactionStatusClassifier.IsCompletedSale(existing))
                    {
                        matchedProduct ??= NayaxProductMatcher.Match(
                            products, existing.NayaxProductId, existing.ProductName);
                        if (matchedProduct is not null &&
                            (!affected.TryGetValue(matchedProduct.Id, out var existingAt) ||
                             existing.MachineAuthorizationTime < existingAt))
                            affected[matchedProduct.Id] = existing.MachineAuthorizationTime;
                    }
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

    private async Task<decimal?> CalculateDirectProfitAsync(
        List<NayaxSales> sales,
        IReadOnlyList<SiteCommissionAgreement> agreements,
        long? siteId,
        DateTime from,
        DateTime to,
        long machineId)
    {
        if (sales.Count > 0 && (!siteId.HasValue || sales.Any(x => !x.CostOfGoodsSold.HasValue)))
            return null;

        decimal totalRevenue = 0m;
        var resolvedSiteId = siteId.GetValueOrDefault();
        foreach (var sale in sales)
        {
            SiteCommissionAgreement? agreement;
            try
            {
                agreement = EffectiveFinancialConfiguration.ResolveAgreement(
                    agreements, resolvedSiteId, sale.MachineAuthorizationTime);
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (agreement is null && agreements.Count > 0) return null;
            var commission = agreement is null
                ? 0m
                : SiteCommissionCalculator.CommissionAmount(
                    agreement,
                    sale.SettlementValue,
                    PaymentMethodClassifier.Classify(sale.PaymentMethod));
            totalRevenue += sale.SettlementValue - sale.CostOfGoodsSold!.Value - commission;
        }
        var fees = await _nayaxProcessingFees.GetProcessingFeesAsync(from, to, machineId);
        if (fees.HasMissingRates) return null;
        return totalRevenue - fees.TotalFeeIncGst;
    }

    private static string? GetProfitabilityStatus(
        IReadOnlyCollection<NayaxSales> sales,
        IReadOnlyList<SiteCommissionAgreement> agreements,
        long? siteId)
    {
        if (sales.Any(x => !x.CostOfGoodsSold.HasValue))
            return "Unavailable: one or more completed sales have no persisted COGS.";
        if (!siteId.HasValue && sales.Count > 0)
            return "Unavailable: the machine is not mapped to a site.";
        if (siteId.HasValue && agreements.Count > 0 && sales.Any(sale =>
                agreements.Count(x => x.SiteId == siteId.Value &&
                    x.EffectiveFrom.Date <= sale.MachineAuthorizationTime.Date &&
                    (!x.EffectiveTo.HasValue || x.EffectiveTo.Value.Date >= sale.MachineAuthorizationTime.Date)) != 1))
            return "Unavailable: commission agreement coverage is missing or ambiguous.";
        return null;
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
