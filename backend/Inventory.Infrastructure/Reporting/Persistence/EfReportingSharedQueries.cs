using Inventory.Infrastructure.Data;
using Inventory.Application.Commissions;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.Availability;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core query helpers shared by the bookkeeping, daily, reconciliation, machine/product
/// profitability, GST accounting-aid, and dashboard report facts providers
/// (<see cref="EfBookkeepingReportFactsProvider"/>, <see cref="EfDailyReportFactsProvider"/>,
/// <see cref="EfReconciliationReportFactsProvider"/>, <see cref="EfMachineProfitabilityReportFactsProvider"/>,
/// <see cref="EfProductProfitabilityReportFactsProvider"/>, <see cref="EfGstReportFactsProvider"/>,
/// <see cref="EfDashboardReportFactsProvider"/>).
/// They depend on <see cref="AppDbContext"/>, which issue #307 moved into this project, and issue
/// #308 (Persistence 7/8 of #153) moved this adapter family after it, so they are
/// Infrastructure-owned rather than API-owned.
///
/// Since issue #1250 the helpers that select <em>sales</em> also take a
/// <see cref="BusinessDateSalesWindow"/>, which resolves an inclusive range of requested business
/// dates to the UTC instants those business days actually cover - including the site commission
/// those reports subtract, which is calculated over the same window rather than over the requested
/// dates read as UTC. Issue #1252 completed that move: every money report - bookkeeping, the GST
/// accounting aid (through bookkeeping), the daily report, the reports dashboard, reconciliation,
/// machine and product profitability, the standalone Commission report and Transaction Sales -
/// selects its sales this way, and none of these helpers offers a calendar-date form for a figure
/// measured over sale instants any more.
///
/// <see cref="EfTransactionSalesReportFactsProvider"/>, the last migrated adapter, deliberately does
/// not reuse the sale-selecting helpers here: it needs every transaction status (not only completed
/// sales) and its own site-name/product-catalogue facts, so its query shape is not equivalent to any
/// helper here (see the reporting migration track in docs/architecture.md). It does share
/// <see cref="SalesWindow"/> and the coverage query.
/// </summary>
internal static class EfReportingSharedQueries
{
    /// <summary>
    /// The window a report selects its sales over for an inclusive range of <em>business</em> dates
    /// (issue #1250): the two business dates themselves, and the UTC instants of business-day
    /// midnight at the start of the first one and at the start of the day after the last - 23, 24 or
    /// 25 hours per business day.
    ///
    /// <para>It exists so a report cannot accidentally compare the stored UTC instant
    /// <c>MachineAuthorizationTime</c> with a requested calendar date as if the date were UTC, which
    /// ran each "day" from 10 or 11 am to 10 or 11 am in <c>Australia/Sydney</c> and put a sale in
    /// the first hours of a business day in the previous period. The conversion is made once, by the
    /// one calendar port, and every selection a report makes over sale instants takes the same
    /// window with it (AGENTS.md § Reporting dates and filters: "Derive business dates only through
    /// <see cref="IBusinessCalendar"/>").</para>
    ///
    /// <para>Date-only values - reimbursement coverage dates, expense dates, purchase dates - are
    /// deliberately not part of this window and keep their plain calendar-date bounds.</para>
    /// </summary>
    internal readonly record struct BusinessDateSalesWindow(
        DateTime FirstBusinessDate,
        DateTime LastBusinessDate,
        DateTime StartUtc,
        DateTime EndExclusiveUtc)
    {
        /// <summary>
        /// The same period as a <see cref="NayaxProcessingFeeBusinessPeriod"/>, so a report's fees
        /// are charged to exactly the sales its revenue counts and each one is estimated at the rate
        /// effective on the business date that counted it (issue #310's contract, issue #1250's
        /// callers). The fee period's end is inclusive, so it is the last instant before the next
        /// business day's midnight.
        /// </summary>
        public NayaxProcessingFeeBusinessPeriod FeePeriod =>
            new(StartUtc, EndExclusiveUtc.AddTicks(-1), FirstBusinessDate, LastBusinessDate);

        /// <summary>
        /// The same period as a <see cref="SiteCommissionBusinessPeriod"/>, so a report's commission
        /// is calculated over exactly the sales its revenue counts (issue #1250), while the
        /// effective-dated agreement covering each of those sales is resolved from that sale's own
        /// business date (issue #1252).
        /// </summary>
        public SiteCommissionBusinessPeriod CommissionPeriod =>
            new(StartUtc, EndExclusiveUtc, FirstBusinessDate, LastBusinessDate);
    }

    /// <summary>
    /// Resolves an inclusive range of requested business dates to the window above, through the
    /// current business's own calendar. This is the only place a reporting adapter in this family
    /// turns requested dates into sale-instant bounds.
    /// </summary>
    public static BusinessDateSalesWindow SalesWindow(IBusinessCalendar businessCalendar, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(businessCalendar);

        var firstBusinessDate = from.Date;
        var lastBusinessDate = to.Date;
        return new BusinessDateSalesWindow(
            firstBusinessDate,
            lastBusinessDate,
            businessCalendar.StartOfBusinessDayUtc(firstBusinessDate),
            businessCalendar.StartOfBusinessDayUtc(lastBusinessDate.AddDays(1)));
    }

    /// <summary>
    /// The provider coverage overlapping one report period (issue #730 for bookkeeping, issue #731
    /// for every other sales-based report), grouped by what each range accounts for: sale instants,
    /// from a fully successful synchronization or a committed sales file import, and settlement
    /// periods, from a committed settlement import.
    ///
    /// <para>One query shared by every reporting adapter, so the six reports cannot read coverage
    /// differently. No machine predicate, because coverage is about whether provider data was
    /// retrieved for the business at all - a machine filter narrows which stored rows a figure
    /// counts, not which runs happened. No provider predicate either: every connected provider's
    /// data feeds the same combined figures today, and a provider filter is explicitly a later
    /// change (parent #329, P5). Reads go through the tenant query filters on
    /// <see cref="AppDbContext"/>; there is no business predicate here, by design (AGENTS.md
    /// § Tenant ownership and data isolation).</para>
    /// </summary>
    /// <param name="db">The request's tenant-filtered context.</param>
    /// <param name="periodStartUtc">
    /// The inclusive start of the window the caller filtered its own rows by, which is the only
    /// window its coverage may be compared with.
    /// </param>
    /// <param name="periodEndExclusiveUtc">That window's exclusive end.</param>
    /// <param name="containsSalesRows">
    /// Whether the period and scope holds any stored provider sale at all, counted before the
    /// completed-sale filter - never derived from an amount.
    /// </param>
    /// <param name="containsSettlementRows">Whether it holds any imported settlement row.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public static async Task<ReportCoverageFacts> CoverageFactsAsync(
        AppDbContext db,
        DateTime periodStartUtc,
        DateTime periodEndExclusiveUtc,
        bool containsSalesRows,
        bool containsSettlementRows,
        CancellationToken cancellationToken)
    {
        var rows = await db.ProviderDataCoverages.AsNoTracking()
            .Where(x => x.FromUtc < periodEndExclusiveUtc && x.ToUtc > periodStartUtc)
            .Select(x => new { x.Source, x.FromUtc, x.ToUtc })
            .ToListAsync(cancellationToken);

        List<ReportCoverageRange> Ranges(params ProviderDataSource[] sources) =>
            rows.Where(x => sources.Contains(x.Source))
                .Select(x => new ReportCoverageRange(x.FromUtc, x.ToUtc))
                .ToList();

        return new ReportCoverageFacts
        {
            PeriodStartUtc = periodStartUtc,
            PeriodEndExclusiveUtc = periodEndExclusiveUtc,
            SalesCoverage = Ranges(ProviderDataSource.SalesSync, ProviderDataSource.SalesFileImport),
            SettlementCoverage = Ranges(ProviderDataSource.SettlementImport),
            ContainsSalesRows = containsSalesRows,
            ContainsSettlementRows = containsSettlementRows,
        };
    }

    /// <summary>
    /// The coverage facts for a business-date window, measured over exactly the instants the
    /// window's sales were selected between - never the requested calendar dates, which a business
    /// day straddles.
    /// </summary>
    public static Task<ReportCoverageFacts> CoverageFactsAsync(
        AppDbContext db,
        BusinessDateSalesWindow window,
        bool containsSalesRows,
        bool containsSettlementRows,
        CancellationToken cancellationToken) =>
        CoverageFactsAsync(db, window.StartUtc, window.EndExclusiveUtc, containsSalesRows, containsSettlementRows, cancellationToken);

    public static IQueryable<NayaxSales> AllSalesQuery(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId) =>
        db.NayaxSales.AsNoTracking().Where(x => x.MachineAuthorizationTime >= from && x.MachineAuthorizationTime < endExclusive &&
            (!machineId.HasValue || x.MachineID == machineId.Value));

    public static IQueryable<NayaxSales> AllSalesQuery(AppDbContext db, BusinessDateSalesWindow window, long? machineId) =>
        AllSalesQuery(db, window.StartUtc, window.EndExclusiveUtc, machineId);

    public static IQueryable<NayaxSales> SalesQuery(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId) =>
        AllSalesQuery(db, from, endExclusive, machineId).Where(EfNayaxSalesQueries.CompletedSalePredicate);

    public static IQueryable<NayaxSales> SalesQuery(AppDbContext db, BusinessDateSalesWindow window, long? machineId) =>
        SalesQuery(db, window.StartUtc, window.EndExclusiveUtc, machineId);

    // The completed-sale cost projection needs only the sale's own fields (no join against
    // Products), which is enough for both bookkeeping's totals and daily's per-day grouping.
    public static IQueryable<SaleCost> CostQuery(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId) =>
        SalesQuery(db, from, endExclusive, machineId).Select(sale => new SaleCost
        {
            SettlementValue = sale.SettlementValue,
            PaymentMethod = sale.PaymentMethod,
            MachineAuthorizationTime = sale.MachineAuthorizationTime,
            CostOfGoodsSold = sale.CostOfGoodsSold,
            HasCost = sale.CostOfGoodsSold.HasValue
        });

    public static IQueryable<SaleCost> CostQuery(AppDbContext db, BusinessDateSalesWindow window, long? machineId) =>
        CostQuery(db, window.StartUtc, window.EndExclusiveUtc, machineId);

    /// <summary>
    /// The effective-dated site commission coverage, warnings and per-machine amounts for a
    /// business-date window, gated on which completed-sale machine IDs actually have a mapped site.
    /// Shared by <see cref="EfBookkeepingReportFactsProvider"/>,
    /// <see cref="EfDashboardReportFactsProvider"/> and
    /// <see cref="EfMachineProfitabilityReportFactsProvider"/>.
    ///
    /// <para>The commission report is asked for the window's own period (issue #1250), so the
    /// commission <em>amounts</em> it returns - which bookkeeping publishes as <c>SiteCommission</c>
    /// and the reports dashboard republishes - are calculated over exactly the sales the report's
    /// revenue counts, and the completed-sale machine IDs they are gated on are selected between the
    /// same instants. There is no calendar-date form of this helper: issue #1252 moved the last
    /// caller that had one onto the window, so no report can ask for commission over the whole UTC
    /// dates a business day straddles.</para>
    /// </summary>
    public static Task<CommissionResolutionResult> GetMachineCommissionsAsync(
        AppDbContext db, IGetSiteCommissionReport siteCommissions, BusinessDateSalesWindow window,
        long? machineId, CancellationToken cancellationToken) =>
        ResolveMachineCommissionsAsync(db, siteCommissions, window.CommissionPeriod, machineId, cancellationToken);

    private static async Task<CommissionResolutionResult> ResolveMachineCommissionsAsync(
        AppDbContext db, IGetSiteCommissionReport siteCommissions, SiteCommissionBusinessPeriod period,
        long? machineId, CancellationToken cancellationToken)
    {
        var report = await siteCommissions.HandleBusinessPeriod(period, null, cancellationToken);

        // An unusable provider makes the commission report an availability answer rather than a set
        // of zero-commission sites (issue #731). Commission therefore stays *incomplete* here: a
        // site that cannot be identified is not a site that is owed nothing, so the direct and net
        // profit figures this result gates must come out unavailable rather than overstated. This is
        // the established rule, not a new one - AGENTS.md § Site commissions: "No agreement is a
        // valid zero-commission case. Incomplete coverage ... must make affected reports
        // provisional rather than selecting one silently."
        if (!report.IsAvailable)
        {
            var reason = report.UnavailableReason
                ?? SiteCommissionUnavailableReason.NayaxNotConnected;
            return new(
                new Dictionary<long, MachineCommission>(), IsComplete: false,
                HasConfigurationGap: false, HasOverlap: false, HasMissingSiteMapping: true,
                UsesMultipleRates: false,
                Warnings: [SiteCommissionUnavailableReasons.Describe(reason)]);
        }

        var relevantRows = report.Rows.Where(site => !machineId.HasValue || site.Machines.Any(machine => machine.MachineId == machineId.Value)).ToList();
        var machines = relevantRows.SelectMany(site => site.Machines.Select(machine =>
            (machine.MachineId, new MachineCommission(site.CommissionRate, machine.CommissionDue, machine.IsComplete))))
            .ToDictionary(x => x.MachineId, x => x.Item2);
        var selectedMachine = machineId.HasValue
            ? relevantRows.SelectMany(x => x.Machines).SingleOrDefault(x => x.MachineId == machineId.Value)
            : null;
        var warnings = machineId.HasValue
            ? SelectedMachineCommissionWarnings(selectedMachine)
            : relevantRows.Where(x => !string.IsNullOrWhiteSpace(x.DataQuality))
                .Select(x => x.DataQuality!).Distinct().ToList();
        var hasConfigurationGap = machineId.HasValue
            ? selectedMachine?.HasConfigurationGap ?? false
            : relevantRows.Any(x => x.HasConfigurationGap);
        var hasOverlap = machineId.HasValue
            ? selectedMachine?.HasOverlap ?? false
            : relevantRows.Any(x => x.HasOverlap);
        var usesMultipleRates = !machineId.HasValue && relevantRows.Any(x => x.UsesMultipleRates);
        var hasMissingSiteMapping = false;
        var saleMachineIds = await SalesQuery(db, period.StartUtc, period.EndExclusiveUtc, machineId).Select(x => x.MachineID).Distinct().ToListAsync(cancellationToken);
        if (saleMachineIds.Any(id => !machines.ContainsKey(id)))
        {
            warnings.Add("Current site mapping is unavailable for one or more completed sales; commission and profitability are incomplete.");
            hasMissingSiteMapping = true;
        }
        return new(machines, !hasConfigurationGap && !hasOverlap && !hasMissingSiteMapping,
            hasConfigurationGap, hasOverlap, hasMissingSiteMapping, usesMultipleRates, warnings.Distinct().ToList());
    }

    private static List<string> SelectedMachineCommissionWarnings(SiteCommissionMachineReportRow? machine)
    {
        var warnings = new List<string>();
        if (machine?.HasConfigurationGap == true)
            warnings.Add("Commission agreements exist but do not cover one or more sales for the selected machine.");
        if (machine?.HasOverlap == true)
            warnings.Add("Overlapping commission agreements cover one or more sales for the selected machine.");
        return warnings;
    }

    public static Task<decimal> GetSiteCommissionAsync(
        AppDbContext db, BusinessDateSalesWindow window, long? machineId, CommissionResolutionResult commissions, CancellationToken cancellationToken) =>
        GetSiteCommissionAsync(db, window.StartUtc, window.EndExclusiveUtc, machineId, commissions, cancellationToken);

    public static async Task<decimal> GetSiteCommissionAsync(
        AppDbContext db, DateTime from, DateTime endExclusive, long? machineId, CommissionResolutionResult commissions, CancellationToken cancellationToken)
    {
        var salesByMachine = await SalesQuery(db, from, endExclusive, machineId)
            .GroupBy(x => x.MachineID)
            .Select(g => new { MachineId = g.Key, Sales = g.Sum(x => x.SettlementValue) })
            .ToListAsync(cancellationToken);
        return salesByMachine.Sum(x => commissions.Machines.GetValueOrDefault(x.MachineId).Due);
    }

    /// <summary>
    /// Whether one imported payout's own declared settlement period lies <em>wholly inside</em> a
    /// requested reporting range - the single rule that decides which payouts a range may count
    /// (issue #1253). Both the payout's bounds and the range are compared as date-only values, the
    /// way <see cref="EfReconciliationReportFactsProvider"/> has always matched a period, and a
    /// payout that declares no period at all lies inside nothing.
    ///
    /// <para>It lives here, beside <see cref="ImportedSummaryAsync"/>, because bookkeeping and
    /// reconciliation must never disagree about which payouts belong to a range: both call this one
    /// predicate instead of re-expressing the comparison, so the boundary convention cannot be
    /// changed in one report and not the other (AGENTS.md § Architecture rules: one authoritative
    /// calculation feeds all presentations). Reconciliation's own per-period matching - its
    /// device/payment/fee navigation graph and card-gross fallback cascade - stays local to that
    /// adapter; only this date comparison is shared.</para>
    /// </summary>
    /// <param name="periodStart">The first date the payout settles, or <c>null</c> when it declares none.</param>
    /// <param name="periodEnd">The last date it settles, inclusive, or <c>null</c> for the same reason.</param>
    /// <param name="from">The first requested date.</param>
    /// <param name="endExclusive">The day after the last requested date, as every caller computes it.</param>
    public static bool IsPeriodWhollyInside(DateTime? periodStart, DateTime? periodEnd, DateTime from, DateTime endExclusive) =>
        periodStart.HasValue && periodEnd.HasValue &&
        periodStart.Value.Date >= from.Date && periodEnd.Value.Date < endExclusive;

    /// <summary>
    /// The period's imported settlement facts, read through the tenant query filters for the
    /// requested date range and machine scope.
    ///
    /// <para>Two different questions are answered from the same rows since issue #1253. A payout
    /// whose declared sales period lies <em>wholly inside</em> the requested range settles only days
    /// the range asked about, so its total is this range's
    /// <see cref="ImportedSummary.FullyCoveredNetSettlement"/>.
    /// A payout the range only <em>partly</em> overlaps settles days outside it as well: its whole
    /// total is not this range's money, it is reported on its own in
    /// <see cref="ImportedSummary.PartlyCoveredPeriods"/> with its own period dates, and it is added
    /// to no total (AGENTS.md § Reimbursements and reconciliation). The wholly-inside rule is the
    /// one <see cref="EfReconciliationReportFactsProvider"/> already matched its periods with, so
    /// the two reports now agree about which payouts belong to a range.</para>
    ///
    /// <para><see cref="ImportedSummary.Settlement"/>, <see cref="ImportedSummary.NetSettlement"/>,
    /// the fee figures and <see cref="ImportedSummary.ContainsRows"/> are deliberately unchanged and
    /// still cover every <em>overlapping</em> row: the fees are prorated per day by their own policy
    /// and the daily report's totals are issue #1262's question, not this one's, while overlapping
    /// settlement data still makes this period's fee figures unverified rather than missing
    /// (issue #731).</para>
    /// </summary>
    public static async Task<ImportedSummary> ImportedSummaryAsync(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate < endExclusive && x.ReimbursementEndDate >= from);
        var reimbursementRows = await reimbursements
            .Select(x => new ImportedReimbursementRow
            {
                Id = x.Id,
                Total = x.Total,
                PayoutDate = x.ReimbursementPayoutDate,
                PeriodStart = x.ReimbursementStartDate,
                PeriodEnd = x.ReimbursementEndDate
            })
            .ToListAsync(cancellationToken);
        if (reimbursementRows.Count == 0)
            return new ImportedSummary(0m, 0m, 0m, 0m, 0m, null, [], [], false, false, false);

        var reimbursementIds = reimbursementRows.Select(x => x.Id).ToList();
        var feeRows = await db.ImportedFees.AsNoTracking()
            .Where(x => reimbursementIds.Contains(x.ImportedReimbursementId))
            .Select(x => new ImportedFeeRow
            {
                ReimbursementId = x.ImportedReimbursementId,
                IsPreviousPeriod = x.IsPreviousPeriod,
                TotalSum = x.TotalSum,
                TotalSumWithVat = x.TotalSumWithVat,
                VatPercentage = x.VatPercentage
            })
            .ToListAsync(cancellationToken);
        var deviceRows = await db.ImportedReimbursementDevices.AsNoTracking()
            .Where(x => reimbursementIds.Contains(x.ImportedReimbursementId))
            .Select(x => new ImportedDeviceRow
            {
                ReimbursementId = x.ImportedReimbursementId,
                EntityId = x.EntityId,
                MachineNumber = x.MachineNumber,
                Gross = x.TotalBillableTransactionAmount ?? 0m,
                NetAmount = x.NetAmount,
                HasNetAmount = x.NetAmount.HasValue
            })
            .ToListAsync(cancellationToken);
        var entityIds = deviceRows
            .Where(x => x.EntityId != null)
            .Select(x => x.EntityId!)
            .Distinct()
            .ToList();
        var paymentRows = await db.ImportedDevicePayments.AsNoTracking()
            .Where(x => reimbursementIds.Contains(x.ImportedReimbursementId) && entityIds.Contains(x.EntityId!))
            .Select(x => new ImportedPaymentRow
            {
                ReimbursementId = x.ImportedReimbursementId,
                EntityId = x.EntityId,
                PaymentMethodDescription = x.PaymentMethodDescription,
                RecognitionDescription = x.RecognitionDescription,
                Amount = x.TotalSum ?? 0m,
                Fees = (x.ProcessingFees ?? 0m) + (x.ServiceFees ?? 0m),
                Count = x.SalesCount ?? 1
            })
            .ToListAsync(cancellationToken);

        var rows = reimbursementRows.Select(x => new ImportedReportRow
        {
            Id = x.Id,
            Settlement = x.Total ?? 0m,
            NetSettlement = x.Total ?? 0m,
            HasImportedTotal = x.Total.HasValue,
            PayoutDate = x.PayoutDate,
            Period = new ImportedReimbursementPeriodDto(
                x.PeriodStart?.Date ?? default, x.PeriodEnd?.Date ?? default, x.Total),
            // Wholly inside the requested range, on the period the settlement itself declares
            // (issue #1253), through the one shared predicate the reconciliation provider matches
            // its own periods with.
            IsFullyCovered = IsPeriodWhollyInside(x.PeriodStart, x.PeriodEnd, from, endExclusive),
            FeesExGst = feeRows.Where(f => f.ReimbursementId == x.Id && !f.IsPreviousPeriod).Sum(f => f.TotalSum ?? 0m),
            FeesIncludingGst = feeRows.Where(f => f.ReimbursementId == x.Id && !f.IsPreviousPeriod).Sum(f => f.TotalSumWithVat ?? f.TotalSum ?? 0m),
            Gst = feeRows.Where(f => f.ReimbursementId == x.Id && !f.IsPreviousPeriod).Sum(f =>
                f.TotalSumWithVat.HasValue && f.TotalSum.HasValue
                    ? f.TotalSumWithVat.Value - f.TotalSum.Value
                    : f.TotalSumWithVat.HasValue && f.VatPercentage.HasValue
                        ? f.TotalSumWithVat.Value * f.VatPercentage.Value / (100m + f.VatPercentage.Value)
                        : 0m),
            HasNet = deviceRows.Any(d => d.ReimbursementId == x.Id && d.HasNetAmount),
            HasGst = feeRows.Any(f => f.ReimbursementId == x.Id && f.VatPercentage.HasValue),
            Devices = deviceRows.Where(d => d.ReimbursementId == x.Id).Select(d => new ImportedReportDevice
            {
                EntityId = d.EntityId,
                MachineNumber = d.MachineNumber,
                Gross = d.Gross,
                NetAmount = d.NetAmount,
                Payments = paymentRows.Where(p => p.ReimbursementId == x.Id && p.EntityId == d.EntityId)
                    .Select(p => new ImportedPaymentNet(p.PaymentMethodDescription, p.RecognitionDescription, p.Amount, p.Fees, p.Count))
                    .ToList()
            }).ToList()
        }).ToList();

        var importedCardTransactionCount = rows.SelectMany(x => x.Devices)
            .SelectMany(d => d.Payments)
            .Where(p => PaymentMethodClassifier.Classify(p.PaymentMethodDescription, p.RecognitionDescription) == NayaxPaymentType.Card)
            .Sum(p => p.Count);

        // The payouts this range may count, and the payouts it only cuts (issue #1253). Ordered by
        // the period they settle so both lists read as a timeline wherever they are published.
        static List<ImportedReimbursementPeriodDto> Periods(IEnumerable<ImportedReportRow> source) =>
            source.OrderBy(x => x.Period.PeriodStart).ThenBy(x => x.Period.PeriodEnd)
                .Select(x => x.Period).ToList();
        var fullyCovered = rows.Where(x => x.IsFullyCovered).ToList();

        if (!machineId.HasValue)
            return new ImportedSummary(rows.Sum(x => x.Settlement), rows.Sum(x => x.FeesExGst),
                rows.Sum(x => x.FeesIncludingGst), rows.Sum(x => x.Gst),
                rows.Sum(x => x.NetSettlement),
                // The actual reimbursement: the wholly-inside payouts' own totals, and *nothing*
                // when none of them states one. A total nobody imported is not a settlement of
                // zero (AGENTS.md § Nayax processing fees and GST: missing provider data is not a
                // zero), so the report says "Not available" rather than comparing against 0.
                fullyCovered.Any(x => x.HasImportedTotal) ? fullyCovered.Sum(x => x.NetSettlement) : null,
                Periods(fullyCovered), Periods(rows.Where(x => !x.IsFullyCovered)),
                rows.Count != 0, rows.Any(x => x.HasGst), rows.Count != 0,
                // Never empty here: the early return above left when no reimbursement row overlapped.
                rows.Select(x => x.PayoutDate).First(), importedCardTransactionCount);

        var matchingDevices = rows.SelectMany(x => x.Devices)
            .Where(d => long.TryParse(d.MachineNumber, out var parsed) && parsed == machineId.Value)
            .ToList();
        var matchingReimbursementIds = rows
            .Where(row => row.Devices.Any(device => long.TryParse(device.MachineNumber, out var parsed) && parsed == machineId.Value))
            .Select(row => row.Id)
            .ToHashSet();
        var matchingRows = rows.Where(row => matchingReimbursementIds.Contains(row.Id)).ToList();
        var matchingSettlement = matchingDevices
            .Sum(d => d.Payments.Count != 0
                ? d.Payments.Where(p => PaymentMethodClassifier.Classify(p.PaymentMethodDescription, p.RecognitionDescription) == NayaxPaymentType.Card).Sum(p => p.Amount)
                : d.Gross);
        // The machine-filtered report answers the same two questions over the payouts that have a
        // device row for the selected machine: only those lying wholly inside the range are its
        // actual reimbursement, and the ones the range cuts are listed beside it (issue #1253).
        var matchingFullyCovered = matchingRows.Where(x => x.IsFullyCovered).ToList();
        return new ImportedSummary(matchingSettlement, 0m,
            0m, 0m,
            matchingRows.Sum(x => x.NetSettlement),
            matchingFullyCovered.Any(x => x.HasImportedTotal) ? matchingFullyCovered.Sum(x => x.NetSettlement) : null,
            Periods(matchingFullyCovered), Periods(matchingRows.Where(x => !x.IsFullyCovered)),
            rows.Count != 0, matchingRows.Any(x => x.HasGst), matchingDevices.Count != 0,
            matchingRows.Select(x => x.PayoutDate).FirstOrDefault(),
            matchingDevices.SelectMany(d => d.Payments)
                .Where(p => PaymentMethodClassifier.Classify(p.PaymentMethodDescription, p.RecognitionDescription) == NayaxPaymentType.Card)
                .Sum(p => p.Count),
            true);
    }

    internal sealed class SaleCost
    {
        public decimal SettlementValue { get; set; }
        public string? PaymentMethod { get; set; }
        public DateTime MachineAuthorizationTime { get; set; }
        public decimal? CostOfGoodsSold { get; set; }
        public bool HasCost { get; set; }
    }

    internal readonly record struct MachineCommission(decimal Percent, decimal Due, bool IsComplete = false);

    internal sealed record CommissionResolutionResult(
        IReadOnlyDictionary<long, MachineCommission> Machines,
        bool IsComplete,
        bool HasConfigurationGap,
        bool HasOverlap,
        bool HasMissingSiteMapping,
        bool UsesMultipleRates,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// The imported settlement facts for one requested range and machine scope.
    ///
    /// <para><c>FullyCoveredNetSettlement</c> is the net total of the payouts whose declared period
    /// lies wholly inside the requested range - the range's <em>actual</em> reimbursement - or
    /// <c>null</c> when no such payout states one at all, which is "Not available" rather than a
    /// settlement of zero (issue #1253). <c>FullyCoveredPeriods</c> is those payouts with the period
    /// dates they settle, and <c>PartlyCoveredPeriods</c> the payouts the range only overlaps, with
    /// their own dates and totals: they are reported so a reader can see what the range cut, and
    /// they are part of no amount here.</para>
    /// </summary>
    internal readonly record struct ImportedSummary(
        decimal Settlement,
        decimal FeesExGst,
        decimal FeesIncludingGst,
        decimal GstOnFees,
        decimal NetSettlement,
        decimal? FullyCoveredNetSettlement,
        IReadOnlyList<ImportedReimbursementPeriodDto> FullyCoveredPeriods,
        IReadOnlyList<ImportedReimbursementPeriodDto> PartlyCoveredPeriods,
        bool ContainsRows,
        bool ContainsGstClassification,
        bool MachineFilterMatched,
        DateTime? PayoutDate = null,
        int CardTransactionCount = 0,
        bool FeesMachineFilterLimited = false);

    private sealed class ImportedReimbursementRow
    {
        public int Id { get; set; }
        public decimal? Total { get; set; }
        public DateTime? PayoutDate { get; set; }
        public DateTime? PeriodStart { get; set; }
        public DateTime? PeriodEnd { get; set; }
    }

    private sealed class ImportedFeeRow
    {
        public int ReimbursementId { get; set; }
        public bool IsPreviousPeriod { get; set; }
        public decimal? TotalSum { get; set; }
        public decimal? TotalSumWithVat { get; set; }
        public decimal? VatPercentage { get; set; }
    }

    private sealed class ImportedDeviceRow
    {
        public int ReimbursementId { get; set; }
        public string? EntityId { get; set; }
        public string? MachineNumber { get; set; }
        public decimal Gross { get; set; }
        public decimal? NetAmount { get; set; }
        public bool HasNetAmount { get; set; }
    }

    private sealed class ImportedPaymentRow
    {
        public int ReimbursementId { get; set; }
        public string? EntityId { get; set; }
        public string? PaymentMethodDescription { get; set; }
        public string? RecognitionDescription { get; set; }
        public decimal Amount { get; set; }
        public decimal Fees { get; set; }
        public int Count { get; set; }
    }

    private sealed class ImportedReportRow
    {
        public int Id { get; set; }
        public decimal Settlement { get; set; }
        public decimal NetSettlement { get; set; }
        public bool HasImportedTotal { get; set; }
        public decimal FeesExGst { get; set; }
        public decimal FeesIncludingGst { get; set; }
        public decimal Gst { get; set; }
        public DateTime? PayoutDate { get; set; }
        public ImportedReimbursementPeriodDto Period { get; set; } = new(default, default, null);
        public bool IsFullyCovered { get; set; }
        public bool HasNet { get; set; }
        public bool HasGst { get; set; }
        public List<ImportedReportDevice> Devices { get; set; } = new();
    }

    private sealed class ImportedReportDevice
    {
        public string? EntityId { get; set; }
        public string? MachineNumber { get; set; }
        public decimal Gross { get; set; }
        public decimal? NetAmount { get; set; }
        public List<ImportedPaymentNet> Payments { get; set; } = new();
    }

    private readonly record struct ImportedPaymentNet(
        string? PaymentMethodDescription,
        string? RecognitionDescription,
        decimal Amount,
        decimal Fees,
        int Count = 1);
}
