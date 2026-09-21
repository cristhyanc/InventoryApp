using Inventory.Application.Reporting.Reconciliation;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IReconciliationReportFactsProvider"/>. It lives
/// in InventoryApi, not Inventory.Infrastructure, for the same reason as
/// <see cref="EfBookkeepingReportFactsProvider"/>/<see cref="EfDailyReportFactsProvider"/>: it
/// depends on <see cref="AppDbContext"/>. Move it into Inventory.Infrastructure once the shared
/// AppDbContext and persistence models relocate there.
///
/// Its completed-sale and all-status sales queries are shared with
/// <see cref="EfBookkeepingReportFactsProvider"/>/<see cref="EfDailyReportFactsProvider"/> through
/// <see cref="EfReportingSharedQueries"/>. Its per-reimbursement-period matching, with the
/// <c>Devices</c>/<c>DevicePayments</c>/<c>PaymentMethods</c>/<c>Fees</c> navigation graph and the
/// card-gross fallback cascade between them, is specific to reconciliation and has no bookkeeping
/// or daily equivalent, so it stays local to this adapter.
/// </summary>
public sealed class EfReconciliationReportFactsProvider : IReconciliationReportFactsProvider
{
    private readonly AppDbContext _db;

    public EfReconciliationReportFactsProvider(AppDbContext db) => _db = db;

    public async Task<ReconciliationReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);
        var isMachineFiltered = machineId.HasValue;

        var paymentSummary = await PaymentSummaryAsync(from, endExclusive, machineId, cancellationToken);
        var sales = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId).ToListAsync(cancellationToken);
        var statusSales = await EfReportingSharedQueries.AllSalesQuery(_db, from, endExclusive, machineId).ToListAsync(cancellationToken);
        var reimbursements = await _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate.HasValue && x.ReimbursementEndDate.HasValue)
            .Include(x => x.Devices)
            .Include(x => x.DevicePayments)
            .Include(x => x.PaymentMethods)
            .Include(x => x.Fees)
            .ToListAsync(cancellationToken);

        // Reconciliation is deliberately period based: an overlapping reimbursement is not
        // silently attributed to a different requested period.
        var matching = reimbursements
            .Where(x => x.ReimbursementStartDate!.Value.Date >= from.Date && x.ReimbursementEndDate!.Value.Date <= to.Date)
            .OrderBy(x => x.Id)
            .ToList();

        var periods = new List<ReconciliationPeriodFacts>();
        foreach (var reimbursement in matching)
        {
            var periodSales = sales.Where(x => x.MachineAuthorizationTime.Date >= reimbursement.ReimbursementStartDate!.Value.Date &&
                x.MachineAuthorizationTime.Date <= reimbursement.ReimbursementEndDate!.Value.Date).ToList();
            periods.Add(BuildPeriodFacts(reimbursement, periodSales, machineId));
        }
        if (periods.Count == 0)
            periods.Add(BuildPeriodFacts(null, sales, machineId, from, to));

        return new ReconciliationReportFacts(
            periods,
            HasMatchedReimbursement: matching.Count != 0,
            TotalVendingSales: paymentSummary.GrossSales,
            CardSales: paymentSummary.CardSales,
            CashSales: paymentSummary.CashSales,
            TotalTransactionCount: sales.Count,
            CardTransactionCount: paymentSummary.CardTransactions,
            CashTransactionCount: paymentSummary.CashTransactions,
            UnknownPaymentTransactionCount: paymentSummary.UnknownTransactions,
            PendingTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
            RefundedTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
            DeclinedOrCancelledTransactionCount: statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
            UnknownStatusTransactionCount: statusSales.Count(x => x.TransactionStatusId is not null && NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown),
            NullStatusTransactionCount: statusSales.Count(x => x.TransactionStatusId is null),
            IsMachineFiltered: isMachineFiltered);
    }

    private static ReconciliationPeriodFacts BuildPeriodFacts(
        ImportedReimbursement? reimbursement, IReadOnlyList<NayaxSales> periodSales, long? machineId,
        DateTime? fallbackFrom = null, DateTime? fallbackTo = null)
    {
        var totalVendingSales = periodSales.Sum(x => x.SettlementValue);
        var cardSales = periodSales.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card)
            .Sum(x => x.SettlementValue);
        var cashSales = periodSales.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash)
            .Sum(x => x.SettlementValue);
        var cardTransactionCount = periodSales.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card);
        var hasImported = reimbursement is not null;

        var devices = reimbursement?.Devices
            .Where(x => !machineId.HasValue || (long.TryParse(x.MachineNumber, out var parsed) && parsed == machineId.Value))
            .ToList() ?? new List<ImportedReimbursementDevice>();
        var paymentRows = devices.Count == 0
            ? (!machineId.HasValue ? reimbursement?.DevicePayments.ToList() ?? new List<ImportedDevicePayment>() : new List<ImportedDevicePayment>())
            : reimbursement!.DevicePayments.Where(x =>
                devices.Any(d => d.EntityId is not null && d.EntityId == x.EntityId) ||
                (devices.Count == 1 && devices[0].EntityId is null && x.EntityId is null)).ToList();
        var hasPaymentRows = paymentRows.Count != 0;
        var paymentMethodRows = !machineId.HasValue
            ? reimbursement?.PaymentMethods.Where(x => !x.IsPreviousPeriod).ToList() ?? new List<ImportedPaymentMethod>()
            : new List<ImportedPaymentMethod>();
        var hasAccountPaymentRows = paymentMethodRows.Count != 0;
        var reportedGross = hasPaymentRows
            ? paymentRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                .Sum(x => x.TotalSum ?? 0m)
            : hasAccountPaymentRows
                ? paymentMethodRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                    .Sum(x => x.TotalSalesSum ?? 0m)
            : devices.Count != 0
                ? devices.Sum(x => x.TotalBillableTransactionAmount ?? 0m)
                : machineId.HasValue ? 0m : reimbursement?.Total ?? 0m;
        var reportedCount = hasPaymentRows
            ? paymentRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                .Sum(x => x.SalesCount ?? 1)
            : hasAccountPaymentRows
                ? paymentMethodRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                    .Sum(x => x.TotalSalesCount ?? 1)
            : devices.Sum(x => x.TotalBillableTransactionCount ?? 0);

        var fees = reimbursement?.Fees.Where(x => !x.IsPreviousPeriod).ToList() ?? new List<ImportedFee>();
        var processingFees = fees.Where(IsProcessingFee).Sum(FeeExGst);
        var otherFees = fees.Where(x => !IsProcessingFee(x)).Sum(FeeExGst);
        var feeGst = fees.Sum(FeeGst);
        var actualNet = reimbursement?.Total ?? 0m;
        var paymentDetailMissing = hasImported && !hasPaymentRows && !hasAccountPaymentRows;
        var warning = machineId.HasValue || paymentDetailMissing ||
            periodSales.Any(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Unknown);
        var hasGstClassification = fees.Any(x => x.VatPercentage.HasValue);

        return new ReconciliationPeriodFacts(
            reimbursement?.ReimbursementStartDate?.Date ?? fallbackFrom!.Value.Date,
            reimbursement?.ReimbursementEndDate?.Date ?? fallbackTo!.Value.Date,
            reimbursement?.ReimbursementPayoutDate,
            hasImported,
            totalVendingSales, cardSales, cashSales, cardTransactionCount,
            periodSales.Count, periodSales.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash),
            reportedGross, reportedCount,
            processingFees, feeGst, otherFees,
            hasGstClassification, warning, paymentDetailMissing, actualNet);
    }

    private static bool IsProcessingFee(ImportedFee fee) =>
        (fee.FeesTypeId ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase) ||
        (fee.FeeTypeDescription ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase);

    private static decimal FeeGst(ImportedFee fee)
    {
        if (fee.TotalSumWithVat.HasValue && fee.TotalSum.HasValue)
            return fee.TotalSumWithVat.Value - fee.TotalSum.Value;
        if (fee.TotalSumWithVat.HasValue && fee.VatPercentage.HasValue)
            return fee.TotalSumWithVat.Value * fee.VatPercentage.Value / (100m + fee.VatPercentage.Value);
        return 0m;
    }

    private static decimal FeeExGst(ImportedFee fee) =>
        fee.TotalSum ?? (fee.TotalSumWithVat.HasValue ? fee.TotalSumWithVat.Value - FeeGst(fee) : 0m);

    private async Task<SalesPaymentSummary> PaymentSummaryAsync(DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        var rows = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId)
            .Select(x => new { x.PaymentMethod, x.SettlementValue })
            .ToListAsync(cancellationToken);
        var classified = rows.GroupBy(x => PaymentMethodClassifier.Classify(x.PaymentMethod))
            .ToDictionary(x => x.Key, x => new { Sales = x.Sum(y => y.SettlementValue), Count = x.Count() });
        var card = classified.GetValueOrDefault(NayaxPaymentType.Card);
        var cash = classified.GetValueOrDefault(NayaxPaymentType.Cash);
        var unknown = classified.GetValueOrDefault(NayaxPaymentType.Unknown);
        return new SalesPaymentSummary(
            rows.Sum(x => x.SettlementValue), rows.Count,
            card?.Sales ?? 0m, card?.Count ?? 0,
            cash?.Sales ?? 0m, cash?.Count ?? 0,
            unknown?.Sales ?? 0m, unknown?.Count ?? 0);
    }

    private readonly record struct SalesPaymentSummary(
        decimal GrossSales,
        int Transactions,
        decimal CardSales,
        int CardTransactions,
        decimal CashSales,
        int CashTransactions,
        decimal UnknownSales,
        int UnknownTransactions);
}
