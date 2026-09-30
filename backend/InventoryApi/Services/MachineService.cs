using Inventory.Application.Machines;
using Inventory.Domain.Reporting;
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
/// <see cref="GetMachineProducts"/> stays here: it returns the EF <see cref="Product"/> entity
/// directly, which belongs to the sibling Products migration (issue #240), not this slice.
/// </summary>
public class MachineService : IMachineService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly GetMachineDashboard _getMachineDashboard;
    private readonly ListMachineDashboard _listMachineDashboard;

    public MachineService(
        AppDbContext db,
        INayaxLynxClient nayaxLynxClient,
        GetMachineDashboard getMachineDashboard,
        ListMachineDashboard listMachineDashboard)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _getMachineDashboard = getMachineDashboard;
        _listMachineDashboard = listMachineDashboard;
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
}
