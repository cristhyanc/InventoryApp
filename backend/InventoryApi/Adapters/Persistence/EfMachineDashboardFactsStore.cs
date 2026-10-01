using Inventory.Application.Machines;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Machines;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IMachineDashboardFactsStore"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and
/// persistence models that still live in InventoryApi, and because it composes the Application-owned
/// <see cref="IGetNayaxProcessingFees"/> use case. Its financial and classification rules are
/// Domain-owned (see <c>docs/architecture.md</c>). Move it into Inventory.Infrastructure once the
/// shared AppDbContext and persistence models relocate there.
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
        long machineId, long? siteId, DateTime now, CancellationToken cancellationToken)
    {
        var today = now.Date;
        var currentWeek = MachineDashboardPeriods.WeekToDate(now);
        var previousComparableWeek = MachineDashboardPeriods.PreviousComparableWeek(now);
        var lastWeek = MachineDashboardPeriods.WeekRange(today, -1);
        var monthToDate = MachineDashboardPeriods.MonthToDate(now);
        var twoWeeksAgo = MachineDashboardPeriods.WeekRange(today, -2);

        var lastSales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.MachineID == machineId && sale.MachineAuthorizationTime > now.AddMonths(-1))
            .ToListAsync(cancellationToken);

        var agreementFrom = lastSales.Count == 0 ? today : lastSales.Min(sale => sale.MachineAuthorizationTime.Date);
        var agreementEntities = siteId.HasValue
            ? await _db.SiteCommissionAgreements.AsNoTracking()
                .Where(agreement => agreement.SiteId == siteId.Value &&
                    agreement.EffectiveFrom <= now &&
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

        var today0 = await BuildPeriodFactsAsync(CompletedSales(lastSales, today, now), agreements, siteId, today, now, machineId, cancellationToken);
        var currentWeekFacts = await BuildPeriodFactsAsync(CompletedSales(lastSales, currentWeek.Start, currentWeek.End), agreements, siteId, currentWeek.Start, currentWeek.End, machineId, cancellationToken);
        var previousComparableWeekFacts = await BuildPeriodFactsAsync(CompletedSales(lastSales, previousComparableWeek.Start, previousComparableWeek.End), agreements, siteId, previousComparableWeek.Start, previousComparableWeek.End, machineId, cancellationToken);
        var lastWeekFacts = await BuildPeriodFactsAsync(CompletedSales(lastSales, lastWeek.Start, lastWeek.End), agreements, siteId, lastWeek.Start, lastWeek.End, machineId, cancellationToken);
        var monthToDateFacts = await BuildPeriodFactsAsync(CompletedSales(lastSales, monthToDate.Start, monthToDate.End), agreements, siteId, monthToDate.Start, monthToDate.End, machineId, cancellationToken);
        var twoWeeksAgoFacts = await BuildPeriodFactsAsync(CompletedSales(lastSales, twoWeeksAgo.Start, twoWeeksAgo.End), agreements, siteId, twoWeeksAgo.Start, twoWeeksAgo.End, machineId, cancellationToken);

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

    private static List<NayaxSales> CompletedSales(List<NayaxSales> sales, DateTime start, DateTime end) =>
        sales.Where(sale => sale.MachineAuthorizationTime >= start && sale.MachineAuthorizationTime <= end &&
                             NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
            .ToList();

    private async Task<MachineDashboardPeriodFacts> BuildPeriodFactsAsync(
        List<NayaxSales> sales,
        IReadOnlyList<CommissionAgreement> agreements,
        long? siteId,
        DateTime from,
        DateTime to,
        long machineId,
        CancellationToken cancellationToken)
    {
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

        var fees = await _nayaxProcessingFees.Handle(from, to, machineId, cancellationToken);
        return new MachineDashboardPeriodFacts(
            grossRevenue,
            new MachineDashboardDirectProfitInputs(false, netSalesBeforeFees, fees.HasMissingRates, fees.TotalFeeIncGst));
    }
}
