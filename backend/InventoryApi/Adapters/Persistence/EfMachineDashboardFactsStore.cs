using Inventory.Application.Machines;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Machines;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IMachineDashboardFactsStore"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and the persistence models
/// it depends on moved there in issue #307, and moving this adapter family after them is
/// Persistence 7/8 and 8/8 of #153. It also composes the Application-owned
/// <see cref="IGetNayaxProcessingFees"/> use case. Its financial and classification rules are
/// Domain-owned (see <c>docs/architecture.md</c>).
/// </summary>
public sealed class EfMachineDashboardFactsStore : IMachineDashboardFactsStore
{
    private readonly AppDbContext _db;
    private readonly IGetNayaxProcessingFees _nayaxProcessingFees;

    public EfMachineDashboardFactsStore(AppDbContext db, IGetNayaxProcessingFees nayaxProcessingFees)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
    }

    public async Task<MachineDashboardFacts> GetFactsAsync(
        long machineId, long? siteId, MachineDashboardWindow window, CancellationToken cancellationToken)
    {
        // Every period boundary is an already-resolved UTC instant derived from the Australia/Sydney
        // business day by the use case (issue #310), and MachineAuthorizationTime is a persisted true
        // UTC instant, so the comparisons below stay in one time base. This adapter no longer decides
        // which day "today" is.
        var lastSales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.MachineID == machineId && sale.MachineAuthorizationTime > window.NowUtc.AddMonths(-1))
            .ToListAsync(cancellationToken);

        var agreementFrom = lastSales.Count == 0
            ? window.BusinessToday
            : lastSales.Min(sale => sale.MachineAuthorizationTime.Date);
        var agreementEntities = siteId.HasValue
            ? await _db.SiteCommissionAgreements.AsNoTracking()
                .Where(agreement => agreement.SiteId == siteId.Value &&
                    agreement.EffectiveFrom <= window.BusinessToday &&
                    (agreement.EffectiveTo == null || agreement.EffectiveTo >= agreementFrom))
                .OrderBy(agreement => agreement.EffectiveFrom)
                .ToListAsync(cancellationToken)
            : [];
        var agreements = agreementEntities.Select(agreement => new CommissionAgreement(
            agreement.Id,
            agreement.SiteId,
            agreement.EffectiveFrom,
            agreement.EffectiveTo,
            agreement.CommissionRate,
            agreement.Frequency,
            agreement.Basis,
            agreement.PaymentDueDaysAfterPeriodEnd,
            agreement.CreatedAt,
            agreement.UpdatedAt)).ToList();

        var today0 = await BuildPeriodFactsAsync(lastSales, agreements, siteId, window.Today, machineId, cancellationToken);
        var currentWeekFacts = await BuildPeriodFactsAsync(lastSales, agreements, siteId, window.CurrentWeek, machineId, cancellationToken);
        var previousComparableWeekFacts = await BuildPeriodFactsAsync(lastSales, agreements, siteId, window.PreviousComparableWeek, machineId, cancellationToken);
        var lastWeekFacts = await BuildPeriodFactsAsync(lastSales, agreements, siteId, window.LastWeek, machineId, cancellationToken);
        var monthToDateFacts = await BuildPeriodFactsAsync(lastSales, agreements, siteId, window.MonthToDate, machineId, cancellationToken);
        var twoWeeksAgoFacts = await BuildPeriodFactsAsync(lastSales, agreements, siteId, window.TwoWeeksAgo, machineId, cancellationToken);

        var completedSales = lastSales.Where(sale => NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId)).ToList();
        var statusInputs = new MachineProfitabilityStatusInputs(
            completedSales.Any(sale => !sale.CostOfGoodsSold.HasValue),
            completedSales.Count > 0,
            siteId.HasValue,
            siteId.HasValue && agreements.Count > 0 && completedSales.Any(sale =>
                agreements.Count(agreement => agreement.SiteId == siteId.Value &&
                    agreement.EffectiveFrom.Date <= sale.MachineAuthorizationTime.Date &&
                    (!agreement.EffectiveTo.HasValue || agreement.EffectiveTo.Value.Date >= sale.MachineAuthorizationTime.Date)) != 1));

        return new MachineDashboardFacts(
            today0, currentWeekFacts, previousComparableWeekFacts, lastWeekFacts, monthToDateFacts, twoWeeksAgoFacts, statusInputs);
    }

    private static List<NayaxSales> CompletedSales(List<NayaxSales> sales, MachineDashboardPeriodUtc period) =>
        sales.Where(sale => sale.MachineAuthorizationTime >= period.StartUtc &&
                             sale.MachineAuthorizationTime <= period.EndUtc &&
                             NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
            .ToList();

    private async Task<MachineDashboardPeriodFacts> BuildPeriodFactsAsync(
        List<NayaxSales> allSales,
        IReadOnlyList<CommissionAgreement> agreements,
        long? siteId,
        MachineDashboardPeriodUtc period,
        long machineId,
        CancellationToken cancellationToken)
    {
        var sales = CompletedSales(allSales, period);
        var grossRevenue = sales.Sum(sale => sale.SettlementValue);

        if (sales.Count > 0 && (!siteId.HasValue || sales.Any(sale => !sale.CostOfGoodsSold.HasValue)))
            return new MachineDashboardPeriodFacts(grossRevenue, new MachineDashboardDirectProfitInputs(true, 0m, false, 0m));

        var resolvedSiteId = siteId.GetValueOrDefault();
        var netSalesBeforeFees = 0m;
        foreach (var sale in sales)
        {
            CommissionAgreement? agreement;
            try
            {
                agreement = EffectiveFinancialConfiguration.ResolveAgreement(agreements, resolvedSiteId, sale.MachineAuthorizationTime);
            }
            catch (InvalidOperationException)
            {
                return new MachineDashboardPeriodFacts(grossRevenue, new MachineDashboardDirectProfitInputs(true, 0m, false, 0m));
            }

            if (agreement is null && agreements.Count > 0)
                return new MachineDashboardPeriodFacts(grossRevenue, new MachineDashboardDirectProfitInputs(true, 0m, false, 0m));

            var commission = agreement is null
                ? 0m
                : SiteCommissionCalculator.CommissionAmount(agreement, sale.SettlementValue, PaymentMethodClassifier.Classify(sale.PaymentMethod));
            netSalesBeforeFees += sale.SettlementValue - sale.CostOfGoodsSold!.Value - commission;
        }

        // The fee use case takes each period's own bounds, as it always has, but as a business-day
        // period rather than a pair of instants it would truncate to whole UTC dates: the fees this
        // period's profit subtracts are charged to exactly the sales its revenue counted above, on the
        // Australia/Sydney business dates the period covers (issue #310).
        var fees = await _nayaxProcessingFees.HandleBusinessPeriod(
            new NayaxProcessingFeeBusinessPeriod(
                period.StartUtc, period.EndUtc, period.FirstBusinessDate, period.LastBusinessDate),
            machineId,
            cancellationToken);
        return new MachineDashboardPeriodFacts(
            grossRevenue,
            new MachineDashboardDirectProfitInputs(false, netSalesBeforeFees, fees.HasMissingRates, fees.TotalFeeIncGst));
    }
}
