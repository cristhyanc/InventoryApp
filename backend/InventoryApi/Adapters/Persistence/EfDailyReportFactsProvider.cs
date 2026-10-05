using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IDailyReportFactsProvider"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure, for the same reason as
/// <see cref="EfBookkeepingReportFactsProvider"/>: it depends on <see cref="AppDbContext"/>, which
/// moved to Inventory.Infrastructure in issue #307, and moving this adapter family after it is
/// Persistence 7/8 and 8/8 of #153. It also composes the Application-owned
/// <see cref="IGetNayaxProcessingFees"/> use case.
///
/// Its completed-sale cost query and period-level imported-reimbursement summary are shared with
/// <see cref="EfBookkeepingReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// Its per-date reimbursement grouping (<see cref="DailyImportedSummaryAsync"/>) is specific to the
/// daily report and has no bookkeeping equivalent, so it is not part of that shared class.
/// </summary>
public sealed class EfDailyReportFactsProvider : IDailyReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IGetNayaxProcessingFees _nayaxProcessingFees;
    private readonly IBusinessCalendar _businessCalendar;

    public EfDailyReportFactsProvider(
        AppDbContext db, IGetNayaxProcessingFees nayaxProcessingFees, IBusinessCalendar businessCalendar)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
        _businessCalendar = businessCalendar;
    }

    public async Task<DailyReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        // Two different kinds of boundary (issue #380). Sales are selected by instant, between Sydney
        // midnight at the start of the first requested business date and, exclusively, Sydney midnight
        // at the start of the day after the last one - 23, 24 or 25 hours per day. Imported
        // reimbursement coverage dates are date-only values, so they keep plain calendar-date bounds
        // and are never shifted by a timezone.
        var endExclusive = to.Date.AddDays(1);
        var salesStartUtc = _businessCalendar.StartOfBusinessDayUtc(from.Date);
        var salesEndExclusiveUtc = _businessCalendar.StartOfBusinessDayUtc(endExclusive);

        var sales = await EfReportingSharedQueries.CostQuery(_db, salesStartUtc, salesEndExclusiveUtc, machineId).ToListAsync(cancellationToken);
        var statusSales = await EfReportingSharedQueries.AllSalesQuery(_db, salesStartUtc, salesEndExclusiveUtc, machineId).ToListAsync(cancellationToken);
        var importedByDate = await DailyImportedSummaryAsync(from, to, endExclusive, machineId, cancellationToken);
        var importedPeriod = await EfReportingSharedQueries.ImportedSummaryAsync(_db, from, endExclusive, machineId, cancellationToken);

        // Each day's fees are charged to exactly the sales that day's revenue counts: the fee use case
        // selects them between the same Sydney-midnight instants and buckets them by the same business
        // date, rather than by the whole UTC date a Sydney day straddles.
        var feeByDate = new Dictionary<DateTime, NayaxProcessingFeeResult>();
        foreach (var date in sales.Select(x => BusinessCalendarDate(x.MachineAuthorizationTime)).Distinct())
            feeByDate[date] = await _nayaxProcessingFees.HandleBusinessPeriod(BusinessPeriod(date, date), machineId, cancellationToken);

        var days = sales
            .GroupBy(x => BusinessCalendarDate(x.MachineAuthorizationTime))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var uncosted = g.Where(x => !x.HasCost).ToList();
                var unknownTransactions = g.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Unknown);
                var imported = importedByDate.TryGetValue(g.Key, out var importedValue) ? importedValue : (DailyImportedSummary?)null;
                var statusRows = statusSales.Where(x => BusinessCalendarDate(x.MachineAuthorizationTime) == g.Key).ToList();

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
                    CompletedTransactionCount: statusRows.Count(sale => NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId)),
                    PendingTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
                    DeclinedOrCancelledTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
                    RefundedTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
                    UnknownStatusTransactionCount: statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown));
            })
            .ToList();

        var totalFees = await _nayaxProcessingFees.HandleBusinessPeriod(BusinessPeriod(from.Date, to.Date), machineId, cancellationToken);
        var totals = new DailyReportTotalsFacts(
            PartialCostOfGoods: sales.Sum(x => x.CostOfGoodsSold ?? 0m),
            IsCogsComplete: sales.All(x => x.HasCost),
            ProcessingFees: totalFees,
            ImportedContainsRows: importedPeriod.ContainsRows,
            ImportedReimbursement: importedPeriod.Settlement,
            ImportedNetReimbursement: importedPeriod.NetSettlement,
            CompletedTransactionCount: statusSales.Count(sale => NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId)),
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

    /// <summary>
    /// The <c>Australia/Sydney</c> business date a sale belongs to, as a true date-only value
    /// (issue #380): the same date the Sites/Machines dashboards and Transaction Sales put the sale's
    /// instant on, taken from the business calendar port rather than from the instant's UTC date.
    ///
    /// A daily row's Date is a date-only value the frontend renders with the ordinary <c>date</c>
    /// pipe, so it carries no <see cref="DateTimeKind"/>: carrying DateTimeKind.Utc through would
    /// serialise the day as "...T00:00:00Z", which a browser west of UTC parses as the previous day
    /// (issue #232 requires date-only fields to stay date-only).
    /// </summary>
    private DateTime BusinessCalendarDate(DateTime instant) =>
        DateTime.SpecifyKind(_businessCalendar.ToBusinessDate(instant), DateTimeKind.Unspecified);

    /// <summary>
    /// The fee period for an inclusive range of business dates: its sales between Sydney midnight at
    /// the start of the first date and the last instant before Sydney midnight after the last date,
    /// which is the same set of sales the daily rows and totals select.
    /// </summary>
    private NayaxProcessingFeeBusinessPeriod BusinessPeriod(DateTime firstBusinessDate, DateTime lastBusinessDate) =>
        new(
            _businessCalendar.StartOfBusinessDayUtc(firstBusinessDate),
            _businessCalendar.StartOfBusinessDayUtc(lastBusinessDate.AddDays(1)).AddTicks(-1),
            firstBusinessDate,
            lastBusinessDate);

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
