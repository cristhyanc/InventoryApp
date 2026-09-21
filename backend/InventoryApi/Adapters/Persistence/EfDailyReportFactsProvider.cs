using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Data;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IDailyReportFactsProvider"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, for the same reason as
/// <see cref="EfBookkeepingReportFactsProvider"/>: it depends on <see cref="AppDbContext"/> and the
/// still-InventoryApi-owned <see cref="INayaxProcessingFeeService"/>. Move it into
/// Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there.
///
/// Its completed-sale cost query and period-level imported-reimbursement summary are shared with
/// <see cref="EfBookkeepingReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// Its per-date reimbursement grouping (<see cref="DailyImportedSummaryAsync"/>) is specific to the
/// daily report and has no bookkeeping equivalent, so it is not part of that shared class.
/// </summary>
public sealed class EfDailyReportFactsProvider : IDailyReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;

    public EfDailyReportFactsProvider(AppDbContext db, INayaxProcessingFeeService nayaxProcessingFees)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
    }

    public async Task<DailyReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);

        var sales = await EfReportingSharedQueries.CostQuery(_db, from, endExclusive, machineId).ToListAsync(cancellationToken);
        var statusSales = await EfReportingSharedQueries.AllSalesQuery(_db, from, endExclusive, machineId).ToListAsync(cancellationToken);
        var importedByDate = await DailyImportedSummaryAsync(from, to, endExclusive, machineId, cancellationToken);
        var importedPeriod = await EfReportingSharedQueries.ImportedSummaryAsync(_db, from, endExclusive, machineId, cancellationToken);

        var feeByDate = new Dictionary<DateTime, NayaxProcessingFeeResult>();
        foreach (var date in sales.Select(x => x.MachineAuthorizationTime.Date).Distinct())
            feeByDate[date] = await _nayaxProcessingFees.GetProcessingFeesAsync(date, date, machineId, cancellationToken);

        var days = sales
            .GroupBy(x => x.MachineAuthorizationTime.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var uncosted = g.Where(x => !x.HasCost).ToList();
                var unknownTransactions = g.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Unknown);
                var imported = importedByDate.TryGetValue(g.Key, out var importedValue) ? importedValue : (DailyImportedSummary?)null;
                var statusRows = statusSales.Where(x => x.MachineAuthorizationTime.Date == g.Key).ToList();

                return new DailyReportDayFacts(
                    Date: g.Key,
                    GrossSales: g.Sum(x => x.SettlementValue),
                    TransactionCount: g.Count(),
                    PartialCostOfGoods: g.Sum(x => x.CostOfGoodsSold ?? 0m),
                    IsCogsComplete: uncosted.Count == 0,
                    CardSales: g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card).Sum(x => x.SettlementValue),
                    CashSales: g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash).Sum(x => x.SettlementValue),
                    UncostedTransactionCount: uncosted.Count,
                    UncostedSalesAmount: uncosted.Sum(x => x.SettlementValue),
                    ProcessingFees: feeByDate[g.Key],
                    ImportedReimbursement: imported?.Reimbursement ?? 0m,
                    NetReimbursement: imported?.NetReimbursement ?? 0m,
                    HasImportedReimbursement: imported?.HasReimbursement ?? false,
                    HasPeriodOnlyImportedData: imported?.HasPeriodOnlyData == true,
                    HasDataQualityWarning: uncosted.Count != 0 || unknownTransactions != 0 ||
                        statusRows.Any(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) != NayaxTransactionStatus.Completed &&
                            x.TransactionStatusId is not null),
                    CompletedTransactionCount: statusRows.Count(NayaxTransactionStatusClassifier.IsCompletedSale),
                    PendingTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
                    DeclinedOrCancelledTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
                    RefundedTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
                    UnknownStatusTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown));
            })
            .ToList();

        var totalFees = await _nayaxProcessingFees.GetProcessingFeesAsync(from, to, machineId, cancellationToken);
        var totals = new DailyReportTotalsFacts(
            PartialCostOfGoods: sales.Sum(x => x.CostOfGoodsSold ?? 0m),
            IsCogsComplete: sales.All(x => x.HasCost),
            ProcessingFees: totalFees,
            ImportedContainsRows: importedPeriod.ContainsRows,
            ImportedReimbursement: importedPeriod.Settlement,
            ImportedNetReimbursement: importedPeriod.NetSettlement,
            CompletedTransactionCount: statusSales.Count(NayaxTransactionStatusClassifier.IsCompletedSale),
            PendingTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
            DeclinedOrCancelledTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
            RefundedTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
            UnknownStatusTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown),
            NullStatusTransactionCount: statusSales.Count(x => x.TransactionStatusId is null));

        return new DailyReportFacts(
            Days: days,
            Totals: totals,
            ImportedContainsRows: importedPeriod.ContainsRows,
            ImportedContainsGstClassification: importedPeriod.ContainsGstClassification,
            ImportedFeesMachineFilterLimited: importedPeriod.FeesMachineFilterLimited,
            HasAnyPeriodOnlyImportedData: importedByDate.Values.Any(x => x.HasPeriodOnlyData));
    }

    private async Task<Dictionary<DateTime, DailyImportedSummary>> DailyImportedSummaryAsync(
        DateTime from, DateTime to, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = await _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate.HasValue && x.ReimbursementEndDate.HasValue &&
                x.ReimbursementStartDate < endExclusive && x.ReimbursementEndDate >= from)
            .Select(x => new
            {
                x.Id,
                Start = x.ReimbursementStartDate!.Value,
                End = x.ReimbursementEndDate!.Value,
                x.Total
            })
            .ToListAsync(cancellationToken);
        if (reimbursements.Count == 0)
            return new();

        var ids = reimbursements.Select(x => x.Id).ToList();
        var fees = await _db.ImportedFees.AsNoTracking()
            .Where(x => ids.Contains(x.ImportedReimbursementId) && !x.IsPreviousPeriod)
            .Select(x => new
            {
                x.ImportedReimbursementId,
                x.TotalSum,
                x.TotalSumWithVat,
                x.VatPercentage
            })
            .ToListAsync(cancellationToken);
        var devices = machineId.HasValue
            ? await _db.ImportedReimbursementDevices.AsNoTracking()
                .Where(x => ids.Contains(x.ImportedReimbursementId))
                .Select(x => new { x.ImportedReimbursementId, x.MachineNumber, x.TotalBillableTransactionAmount })
                .ToListAsync(cancellationToken)
            : new();

        var result = new Dictionary<DateTime, DailyImportedSummary>();
        foreach (var reimbursement in reimbursements)
        {
            var start = reimbursement.Start.Date;
            var end = reimbursement.End.Date;
            var matchingMachine = machineId.HasValue && devices.Any(x =>
                x.ImportedReimbursementId == reimbursement.Id &&
                long.TryParse(x.MachineNumber, out var parsed) && parsed == machineId.Value);
            if (machineId.HasValue && !matchingMachine)
                continue;

            var daily = start == end && start >= from && start <= to;
            if (!daily)
            {
                if (start <= to && end >= from)
                {
                    var periodStart = start < from.Date ? from.Date : start;
                    var periodEnd = end > to.Date ? to.Date : end;
                    for (var date = periodStart; date <= periodEnd; date = date.AddDays(1))
                    {
                        var existingPeriod = result.GetValueOrDefault(date);
                        result[date] = existingPeriod with { HasPeriodOnlyData = true };
                    }
                }
                continue;
            }

            var reimbursementFees = fees.Where(x => x.ImportedReimbursementId == reimbursement.Id).ToList();
            var reimbursementAmount = machineId.HasValue
                ? devices.Where(x => x.ImportedReimbursementId == reimbursement.Id &&
                    long.TryParse(x.MachineNumber, out var parsed) && parsed == machineId.Value)
                    .Sum(x => x.TotalBillableTransactionAmount ?? 0m)
                : reimbursement.Total ?? 0m;
            var summary = new DailyImportedSummary(
                reimbursementAmount,
                machineId.HasValue ? 0m : reimbursementFees.Sum(x => x.TotalSum ?? 0m),
                machineId.HasValue ? 0m : reimbursementFees.Sum(x => x.TotalSumWithVat ?? x.TotalSum ?? 0m),
                reimbursementFees.Any(x => x.VatPercentage.HasValue),
                true,
                reimbursement.Total ?? 0m,
                machineId.HasValue);
            result[start] = result.GetValueOrDefault(start) + summary;
        }
        return result;
    }

    private readonly record struct DailyImportedSummary(
        decimal Reimbursement,
        decimal FeesExGst,
        decimal FeesIncludingGst,
        bool HasGstClassification,
        bool HasReimbursement,
        decimal NetReimbursement,
        bool FeesMachineFilterLimited,
        bool HasPeriodOnlyData = false)
    {
        public static DailyImportedSummary operator +(DailyImportedSummary left, DailyImportedSummary right) =>
            new(left.Reimbursement + right.Reimbursement,
                left.FeesExGst + right.FeesExGst,
                left.FeesIncludingGst + right.FeesIncludingGst,
                left.HasGstClassification || right.HasGstClassification,
                left.HasReimbursement || right.HasReimbursement,
                left.NetReimbursement + right.NetReimbursement,
                left.FeesMachineFilterLimited || right.FeesMachineFilterLimited,
                left.HasPeriodOnlyData || right.HasPeriodOnlyData);
    }
}
