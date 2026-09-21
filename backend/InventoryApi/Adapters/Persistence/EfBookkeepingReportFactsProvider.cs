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
/// Its completed-sale cost query and period-level imported-reimbursement summary are shared with
/// <see cref="EfDailyReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>. Its
/// remaining private EF query helpers (receipts, operating expenses, commissions) intentionally
/// mirror equivalent private helpers still used by the other, not-yet-migrated report families in
/// <see cref="InventoryApi.Services.ReportingService"/> (reconciliation, machine/product
/// profitability, GST, dashboard). They are query mechanics, not financial formulas, and will be
/// de-duplicated as those report families are migrated in their own issues (see the reporting
/// migration track in docs/architecture.md).
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

        var paymentRows = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId)
            .Select(x => new { x.PaymentMethod, x.SettlementValue })
            .ToListAsync(cancellationToken);
        var classified = paymentRows.GroupBy(x => PaymentMethodClassifier.Classify(x.PaymentMethod))
            .ToDictionary(x => x.Key, x => new { Sales = x.Sum(y => y.SettlementValue), Count = x.Count() });
        var card = classified.GetValueOrDefault(NayaxPaymentType.Card);
        var cash = classified.GetValueOrDefault(NayaxPaymentType.Cash);
        var unknown = classified.GetValueOrDefault(NayaxPaymentType.Unknown);
        var grossSales = paymentRows.Sum(x => x.SettlementValue);

        var saleCosts = await EfReportingSharedQueries.CostQuery(_db, from, endExclusive, machineId).ToListAsync(cancellationToken);
        var partialCost = saleCosts.Sum(x => x.CostOfGoodsSold ?? 0m);
        var uncosted = saleCosts.Where(x => !x.HasCost).ToList();
        var isCogsComplete = uncosted.Count == 0;

        var (receiptDelivery, receiptPackage) = isMachineFiltered
            ? (0m, 0m)
            : await ReceiptCostsAsync(from, endExclusive, cancellationToken);

        var imported = await EfReportingSharedQueries.ImportedSummaryAsync(_db, from, endExclusive, machineId, cancellationToken);
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
        var saleMachineIds = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId).Select(x => x.MachineID).Distinct().ToListAsync(cancellationToken);
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
        var salesByMachine = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId)
            .GroupBy(x => x.MachineID)
            .Select(g => new { MachineId = g.Key, Sales = g.Sum(x => x.SettlementValue) })
            .ToListAsync(cancellationToken);
        return salesByMachine.Sum(x => commissions.Machines.GetValueOrDefault(x.MachineId).Due);
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
}
