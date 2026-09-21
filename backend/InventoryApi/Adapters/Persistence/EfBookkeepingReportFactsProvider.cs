using Inventory.Application.Reporting.Bookkeeping;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IBookkeepingReportFactsProvider"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and
/// persistence models that still live in InventoryApi, and because it also composes the existing
/// <see cref="INayaxProcessingFeeService"/> and <see cref="ISiteCommissionService"/> business
/// services, which are not part of this migration. Move it into Inventory.Infrastructure once the
/// shared AppDbContext and persistence models relocate there.
///
/// Its private EF query helpers intentionally mirror equivalent private helpers still used by the
/// other, not-yet-migrated report families in <see cref="InventoryApi.Services.ReportingService"/>
/// (daily, reconciliation, machine/product profitability, GST, dashboard). They are query mechanics,
/// not financial formulas, and will be de-duplicated as those report families are migrated in their
/// own issues (see the reporting migration track in docs/architecture.md).
/// </summary>
public sealed class EfBookkeepingReportFactsProvider : IBookkeepingReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;
    private readonly ISiteCommissionService _siteCommissions;

    public EfBookkeepingReportFactsProvider(
        AppDbContext db, INayaxProcessingFeeService nayaxProcessingFees, ISiteCommissionService siteCommissions)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
        _siteCommissions = siteCommissions;
    }

    public async Task<BookkeepingReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);
        var isMachineFiltered = machineId.HasValue;

        var paymentRows = await SalesQuery(from, endExclusive, machineId)
            .Select(x => new { x.PaymentMethod, x.SettlementValue })
            .ToListAsync(cancellationToken);
        var classified = paymentRows.GroupBy(x => PaymentMethodClassifier.Classify(x.PaymentMethod))
            .ToDictionary(x => x.Key, x => new { Sales = x.Sum(y => y.SettlementValue), Count = x.Count() });
        var card = classified.GetValueOrDefault(NayaxPaymentType.Card);
        var cash = classified.GetValueOrDefault(NayaxPaymentType.Cash);
        var unknown = classified.GetValueOrDefault(NayaxPaymentType.Unknown);
        var grossSales = paymentRows.Sum(x => x.SettlementValue);

        var saleCosts = await CostQuery(from, endExclusive, machineId).ToListAsync(cancellationToken);
        var partialCost = saleCosts.Sum(x => x.CostOfGoodsSold ?? 0m);
        var uncosted = saleCosts.Where(x => !x.HasCost).ToList();
        var isCogsComplete = uncosted.Count == 0;

        var (receiptDelivery, receiptPackage) = isMachineFiltered
            ? (0m, 0m)
            : await ReceiptCostsAsync(from, endExclusive, cancellationToken);

        var imported = await ImportedSummaryAsync(from, endExclusive, machineId, cancellationToken);
        var processingFees = await _nayaxProcessingFees.GetProcessingFeesAsync(from, to, machineId, cancellationToken);
        var operatingExpenses = await OperatingExpenseSummaryAsync(from, endExclusive, machineId, cancellationToken);
        var commissions = await GetMachineCommissionsAsync(from, to, endExclusive, machineId, cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(from, endExclusive, machineId, commissions, cancellationToken);

        var commissionCompleteForScope = isMachineFiltered
            ? commissions.Machines.GetValueOrDefault(machineId!.Value).IsComplete
            : commissions.IsComplete;

        return new BookkeepingReportFacts(
            GrossSales: grossSales,
            CardSales: card?.Sales ?? 0m,
            CardTransactions: card?.Count ?? 0,
            CashSales: cash?.Sales ?? 0m,
            CashTransactions: cash?.Count ?? 0,
            UnknownTransactions: unknown?.Count ?? 0,
            PartialCostOfGoods: partialCost,
            IsCogsComplete: isCogsComplete,
            UncostedTransactionCount: uncosted.Count,
            UncostedSalesAmount: uncosted.Sum(x => x.SettlementValue),
            ReceiptDeliveryCost: receiptDelivery,
            ReceiptPackageCost: receiptPackage,
            ImportedHasNetSettlement: imported.HasNetSettlement,
            ImportedNetSettlement: imported.NetSettlement,
            ImportedContainsRows: imported.ContainsRows,
            ImportedContainsGstClassification: imported.ContainsGstClassification,
            ImportedMachineFilterMatched: imported.MachineFilterMatched,
            ImportedFeesMachineFilterLimited: imported.FeesMachineFilterLimited,
            OperatingExpensesTotal: operatingExpenses.Total,
            OperatingExpensesGst: operatingExpenses.Gst,
            OperatingExpensesByCategory: operatingExpenses.ByCategory,
            SiteCommission: siteCommission,
            CommissionCompleteForScope: commissionCompleteForScope,
            CommissionIsComplete: commissions.IsComplete,
            CommissionWarnings: commissions.Warnings,
            ProcessingFees: processingFees);
    }

    private IQueryable<NayaxSales> AllSalesQuery(DateTime from, DateTime endExclusive, long? machineId) =>
        _db.NayaxSales.AsNoTracking().Where(x => x.MachineAuthorizationTime >= from && x.MachineAuthorizationTime < endExclusive &&
            (!machineId.HasValue || x.MachineID == machineId.Value));

    private IQueryable<NayaxSales> SalesQuery(DateTime from, DateTime endExclusive, long? machineId) =>
        AllSalesQuery(from, endExclusive, machineId).Where(NayaxTransactionStatusClassifier.CompletedSalePredicate);

    // Bookkeeping's cost projection needs only the sale's own fields (unlike the still-legacy
    // report methods that group by product), so no join against Products is needed here.
    private IQueryable<SaleCost> CostQuery(DateTime from, DateTime endExclusive, long? machineId) =>
        SalesQuery(from, endExclusive, machineId).Select(sale => new SaleCost
        {
            SettlementValue = sale.SettlementValue,
            CostOfGoodsSold = sale.CostOfGoodsSold,
            HasCost = sale.CostOfGoodsSold.HasValue
        });

    private async Task<(decimal Delivery, decimal Package)> ReceiptCostsAsync(DateTime from, DateTime endExclusive, CancellationToken cancellationToken)
    {
        var costs = await _db.Receipts.AsNoTracking()
            .Where(x => x.PurchaseDate >= from && x.PurchaseDate < endExclusive)
            .GroupBy(_ => 1)
            .Select(g => new { Delivery = g.Sum(x => x.DeliveryCost ?? 0m), Package = g.Sum(x => x.PackageCost ?? 0m) })
            .SingleOrDefaultAsync(cancellationToken);
        return (costs?.Delivery ?? 0m, costs?.Package ?? 0m);
    }

    private async Task<OperatingExpenseSummary> OperatingExpenseSummaryAsync(DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var rows = await _db.OperatingExpenses.AsNoTracking()
            .Where(x => x.ExpenseDate >= from && x.ExpenseDate < endExclusive &&
                (!machineId.HasValue || x.MachineId == machineId.Value))
            .Select(x => new { x.Category, x.TotalAmount, x.GstAmount })
            .ToListAsync(cancellationToken);
        return new OperatingExpenseSummary(rows.Sum(x => x.TotalAmount), rows.Sum(x => x.GstAmount),
            rows.GroupBy(x => x.Category.ToString()).ToDictionary(g => g.Key, g => g.Sum(x => x.TotalAmount)));
    }

    private async Task<ImportedSummary> ImportedSummaryAsync(DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = _db.ImportedReimbursements.AsNoTracking()
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
        var feeRows = await _db.ImportedFees.AsNoTracking()
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
        var deviceRows = await _db.ImportedReimbursementDevices.AsNoTracking()
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
        var paymentRows = await _db.ImportedDevicePayments.AsNoTracking()
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

    private async Task<CommissionResolutionResult> GetMachineCommissionsAsync(
        DateTime from, DateTime to, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var report = await _siteCommissions.GetReportAsync(from, to, null, cancellationToken);

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
        var saleMachineIds = await SalesQuery(from, endExclusive, machineId).Select(x => x.MachineID).Distinct().ToListAsync(cancellationToken);
        if (saleMachineIds.Any(id => !machines.ContainsKey(id)))
        {
            warnings.Add("Current site mapping is unavailable for one or more completed sales; commission and profitability are incomplete.");
            hasMissingSiteMapping = true;
        }
        return new(machines, !hasConfigurationGap && !hasOverlap && !hasMissingSiteMapping,
            hasConfigurationGap, hasOverlap, hasMissingSiteMapping, usesMultipleRates, warnings.Distinct().ToList());
    }

    private static List<string> SelectedMachineCommissionWarnings(SiteCommissionMachineDto? machine)
    {
        var warnings = new List<string>();
        if (machine?.HasConfigurationGap == true)
            warnings.Add("Commission agreements exist but do not cover one or more sales for the selected machine.");
        if (machine?.HasOverlap == true)
            warnings.Add("Overlapping commission agreements cover one or more sales for the selected machine.");
        return warnings;
    }

    private async Task<decimal> GetSiteCommissionAsync(
        DateTime from, DateTime endExclusive, long? machineId, CommissionResolutionResult commissions, CancellationToken cancellationToken)
    {
        var salesByMachine = await SalesQuery(from, endExclusive, machineId)
            .GroupBy(x => x.MachineID)
            .Select(g => new { MachineId = g.Key, Sales = g.Sum(x => x.SettlementValue) })
            .ToListAsync(cancellationToken);
        return salesByMachine.Sum(x => commissions.Machines.GetValueOrDefault(x.MachineId).Due);
    }

    private sealed class SaleCost
    {
        public decimal SettlementValue { get; set; }
        public decimal? CostOfGoodsSold { get; set; }
        public bool HasCost { get; set; }
    }

    private sealed record OperatingExpenseSummary(decimal Total, decimal Gst, IReadOnlyDictionary<string, decimal> ByCategory);

    private readonly record struct MachineCommission(decimal Percent, decimal Due, bool IsComplete = false);

    private sealed record CommissionResolutionResult(
        IReadOnlyDictionary<long, MachineCommission> Machines,
        bool IsComplete,
        bool HasConfigurationGap,
        bool HasOverlap,
        bool HasMissingSiteMapping,
        bool UsesMultipleRates,
        IReadOnlyList<string> Warnings);

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

    private readonly record struct ImportedSummary(
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
}
