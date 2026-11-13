using Inventory.Application.Machines;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Machines;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMachineDashboardFactsStore"/>. It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the persistence models it
/// depends on, which moved there in issue #307; this adapter followed them in issue #309
/// (Persistence 8/8 of #153). It also composes the Application-owned
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
        // Every period boundary is an already-resolved UTC instant derived from the business
        // day by the use case (issue #310), and MachineAuthorizationTime is a persisted true
        // UTC instant, so the comparisons below stay in one time base. This adapter no longer decides
        // which day "today" is.
        //
        // One query, reaching back as far as the earliest published period begins (issue #1180): the
        // one-month lower bound below did not reach the start of the previous calendar month, which
        // the previous comparable month's revenue is summed from. The bound is exclusive, so it is
        // taken one millisecond before that first instant - a sale at exactly a period's StartUtc is
        // inside that period, and must therefore be read.
        var lastMonthFromUtc = window.NowUtc.AddMonths(-1);
        var previousComparableMonthFromUtc = window.PreviousComparableMonth.StartUtc.AddMilliseconds(-1);
        var salesFromUtc = previousComparableMonthFromUtc < lastMonthFromUtc
            ? previousComparableMonthFromUtc
            : lastMonthFromUtc;
        var sales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.MachineID == machineId && sale.MachineAuthorizationTime > salesFromUtc)
            .ToListAsync(cancellationToken);

        // Exactly the sales the commission agreements below and the profitability status at the end
        // were resolved from before the read widened - the last month - so no existing figure, period
        // or status changes (issue #1180). Every period's own figures are selected from the full read
        // by that period's instants, which is what they always were: each published period except the
        // new one lies inside this last month.
        var lastSales = sales.Where(sale => sale.MachineAuthorizationTime > lastMonthFromUtc).ToList();

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

        var today0 = await BuildPeriodFactsAsync(sales, agreements, siteId, window.Today, machineId, cancellationToken);
        var currentWeekFacts = await BuildPeriodFactsAsync(sales, agreements, siteId, window.CurrentWeek, machineId, cancellationToken);
        var previousComparableWeekFacts = await BuildPeriodFactsAsync(sales, agreements, siteId, window.PreviousComparableWeek, machineId, cancellationToken);
        var lastWeekFacts = await BuildPeriodFactsAsync(sales, agreements, siteId, window.LastWeek, machineId, cancellationToken);
        var monthToDateFacts = await BuildPeriodFactsAsync(sales, agreements, siteId, window.MonthToDate, machineId, cancellationToken);
        var twoWeeksAgoFacts = await BuildPeriodFactsAsync(sales, agreements, siteId, window.TwoWeeksAgo, machineId, cancellationToken);

        // Today's baseline is revenue only (issue #1177), so it is summed here from the same
        // completed sales the period figures above are built from and asks the fee engine for
        // nothing: no profit is published for it, so no commission is resolved and no processing fee
        // is looked up for its business dates.
        var previousComparableDayGrossRevenue = CompletedSales(sales, window.PreviousComparableDay)
            .Sum(sale => sale.SettlementValue);

        // Month to date's baseline, on exactly those terms (issue #1180): the same completed-sale
        // policy over the same read, summed between the period's own instants, with no profit, no
        // commission and no fee lookup.
        var previousComparableMonthGrossRevenue = CompletedSales(sales, window.PreviousComparableMonth)
            .Sum(sale => sale.SettlementValue);

        // Yesterday in full, the Dashboard's Today baseline since issue #1215, on exactly those
        // terms. Its first instant is less than two days before now, so the read above already
        // covers it.
        var previousDayGrossRevenue = CompletedSales(sales, window.PreviousDay)
            .Sum(sale => sale.SettlementValue);

        var completedSales = lastSales.Where(sale => NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification)).ToList();
        var statusInputs = new MachineProfitabilityStatusInputs(
            completedSales.Any(sale => !sale.CostOfGoodsSold.HasValue),
            completedSales.Count > 0,
            siteId.HasValue,
            siteId.HasValue && agreements.Count > 0 && completedSales.Any(sale =>
                agreements.Count(agreement => agreement.SiteId == siteId.Value &&
                    agreement.EffectiveFrom.Date <= sale.MachineAuthorizationTime.Date &&
                    (!agreement.EffectiveTo.HasValue || agreement.EffectiveTo.Value.Date >= sale.MachineAuthorizationTime.Date)) != 1));

        return new MachineDashboardFacts(
            today0, currentWeekFacts, previousComparableWeekFacts, lastWeekFacts, monthToDateFacts, twoWeeksAgoFacts,
            previousComparableDayGrossRevenue, previousComparableMonthGrossRevenue, previousDayGrossRevenue,
            statusInputs);
    }

    private static List<NayaxSales> CompletedSales(List<NayaxSales> sales, MachineDashboardPeriodUtc period) =>
        sales.Where(sale => sale.MachineAuthorizationTime >= period.StartUtc &&
                             sale.MachineAuthorizationTime <= period.EndUtc &&
                             NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification))
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
        // business dates the period covers (issue #310).
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
