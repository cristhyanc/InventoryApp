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
/// Its completed-sale cost query, period-level imported-reimbursement summary, and site-commission
/// resolution are shared with <see cref="EfDailyReportFactsProvider"/>,
/// <see cref="EfReconciliationReportFactsProvider"/>, and
/// <see cref="EfMachineProfitabilityReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// Its remaining private EF query helpers (receipts, operating expenses) intentionally mirror
/// equivalent private helpers still used by the transactions report, the only report family in
/// <see cref="InventoryApi.Services.ReportingService"/> not yet migrated. They are query mechanics,
/// not financial formulas, and will be de-duplicated once that report family migrates in its own
/// issue (see the reporting migration track in docs/architecture.md).
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
        var commissions = await EfReportingSharedQueries.GetMachineCommissionsAsync(_db, _siteCommissions, from, to, endExclusive, machineId, cancellationToken);
        var siteCommission = await EfReportingSharedQueries.GetSiteCommissionAsync(_db, from, endExclusive, machineId, commissions, cancellationToken);

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

    private sealed record OperatingExpenseSummary(decimal Total, decimal Gst, IReadOnlyDictionary<string, decimal> ByCategory);
}
