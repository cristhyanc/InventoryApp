using Inventory.Infrastructure.Data;
using Inventory.Application.Commissions;
using Inventory.Application.Reporting.Shared;
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
/// <see cref="EfTransactionSalesReportFactsProvider"/>, the last migrated adapter, deliberately does
/// not reuse these helpers: it needs every transaction status (not only completed sales) and its own
/// site-name/product-catalogue facts, so its query shape is not equivalent to any helper here (see
/// the reporting migration track in docs/architecture.md).
/// </summary>
internal static class EfReportingSharedQueries
{
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

    public static IQueryable<NayaxSales> AllSalesQuery(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId) =>
        db.NayaxSales.AsNoTracking().Where(x => x.MachineAuthorizationTime >= from && x.MachineAuthorizationTime < endExclusive &&
            (!machineId.HasValue || x.MachineID == machineId.Value));

    public static IQueryable<NayaxSales> SalesQuery(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId) =>
        AllSalesQuery(db, from, endExclusive, machineId).Where(EfNayaxSalesQueries.CompletedSalePredicate);

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

    // Shared by EfBookkeepingReportFactsProvider and EfMachineProfitabilityReportFactsProvider: both
    // resolve the same effective-dated site commission coverage/warnings for a date range and
    // optional machine filter, gated on which completed-sale machine IDs actually have a mapped site.
    public static async Task<CommissionResolutionResult> GetMachineCommissionsAsync(
        AppDbContext db, IGetSiteCommissionReport siteCommissions, DateTime from, DateTime to, DateTime endExclusive,
        long? machineId, CancellationToken cancellationToken)
    {
        var report = await siteCommissions.Handle(from, to, null, cancellationToken);

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
        var saleMachineIds = await SalesQuery(db, from, endExclusive, machineId).Select(x => x.MachineID).Distinct().ToListAsync(cancellationToken);
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

    public static async Task<decimal> GetSiteCommissionAsync(
        AppDbContext db, DateTime from, DateTime endExclusive, long? machineId, CommissionResolutionResult commissions, CancellationToken cancellationToken)
    {
        var salesByMachine = await SalesQuery(db, from, endExclusive, machineId)
            .GroupBy(x => x.MachineID)
            .Select(g => new { MachineId = g.Key, Sales = g.Sum(x => x.SettlementValue) })
            .ToListAsync(cancellationToken);
        return salesByMachine.Sum(x => commissions.Machines.GetValueOrDefault(x.MachineId).Due);
    }

    public static async Task<ImportedSummary> ImportedSummaryAsync(AppDbContext db, DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate < endExclusive && x.ReimbursementEndDate >= from);
        var reimbursementRows = await reimbursements
            .Select(x => new ImportedReimbursementRow
            {
                Id = x.Id,
                Total = x.Total,
                PayoutDate = x.ReimbursementPayoutDate
            })
            .ToListAsync(cancellationToken);
        if (reimbursementRows.Count == 0)
            return new ImportedSummary(0m, 0m, 0m, 0m, 0m, false, false, false, false);

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

        if (!machineId.HasValue)
            return new ImportedSummary(rows.Sum(x => x.Settlement), rows.Sum(x => x.FeesExGst),
                rows.Sum(x => x.FeesIncludingGst), rows.Sum(x => x.Gst),
                rows.Sum(x => x.NetSettlement),
                rows.Count != 0, rows.Any(x => x.HasGst), rows.Any(x => x.HasImportedTotal), rows.Count != 0, rows.Select(x => x.PayoutDate).FirstOrDefault(), importedCardTransactionCount);

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
        return new ImportedSummary(matchingSettlement, 0m,
            0m, 0m,
            matchingRows.Sum(x => x.NetSettlement),
            rows.Count != 0, matchingRows.Any(x => x.HasGst), matchingRows.Any(x => x.HasImportedTotal), matchingDevices.Count != 0,
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

    internal readonly record struct ImportedSummary(
        decimal Settlement,
        decimal FeesExGst,
        decimal FeesIncludingGst,
        decimal GstOnFees,
        decimal NetSettlement,
        bool ContainsRows,
        bool ContainsGstClassification,
        bool HasNetSettlement,
        bool MachineFilterMatched,
        DateTime? PayoutDate = null,
        int CardTransactionCount = 0,
        bool FeesMachineFilterLimited = false);

    private sealed class ImportedReimbursementRow
    {
        public int Id { get; set; }
        public decimal? Total { get; set; }
        public DateTime? PayoutDate { get; set; }
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
