using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Reconciliation;

namespace Inventory.Application.Reporting.Reconciliation;

/// <summary>
/// The reconciliation report use case: resolves the requested date range/machine scope, retrieves
/// report facts through the narrow <see cref="IReconciliationReportFactsProvider"/> port, applies
/// the Domain per-period and totals reconciliation policy, and builds the authoritative
/// <see cref="ReconciliationReportDto"/> consumed by both the API response and the CSV/XLSX export.
/// Adjustments are always zero: the imported reimbursement model does not currently support them.
/// </summary>
public sealed class GetReconciliationReport
{
    private const decimal Adjustments = 0m;

    private readonly IReconciliationReportFactsProvider _facts;

    public GetReconciliationReport(IReconciliationReportFactsProvider facts) => _facts = facts;

    public async Task<ReconciliationReportDto> Handle(ReportingFilterDto filter, decimal tolerance, CancellationToken cancellationToken)
    {
        var range = ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);
        var machineId = filter.MachineId ?? filter.MachineID;

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, machineId, cancellationToken);

        var periodRows = facts.Periods.Select(period => BuildPeriodRow(period, facts.IsMachineFiltered, tolerance)).ToList();

        var totals = BuildTotals(facts, periodRows, tolerance);
        var actualNet = totals.ActualNetReimbursement;

        var qualityNotes = new List<string>();
        if (!facts.HasMatchedReimbursement)
            qualityNotes.Add("No imported reimbursement row matched the requested start and end dates.");
        if (facts.UnknownPaymentTransactionCount > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        AddStatusQualityNotes(qualityNotes, facts);
        if (facts.IsMachineFiltered)
            qualityNotes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        qualityNotes.Add("Adjustments are unsupported by the imported reimbursement model and are treated as zero.");
        var quality = ReportingQuality.Quality(facts.HasMatchedReimbursement,
            periodRows.Any(x => x.DataQuality.GstClassificationMissing == false), false,
            string.Join(" ", qualityNotes) is { Length: > 0 } note ? note : null);

        var first = periodRows[0];
        return new ReconciliationReportDto(range.From, range.ToDate, totals.CardTransactionSales,
            totals.NayaxReportedGrossCardSales, totals.GrossDifference, Math.Abs(tolerance),
            facts.HasMatchedReimbursement && ReconciliationStatusPolicy.IsReconciled(totals.GrossDifference, tolerance), quality,
            totals.CardTransactionCount, totals.NayaxReportedCardTransactionCount, totals.CountDifference,
            totals.ProcessingFeesExGst, actualNet, first.PayoutDate)
        {
            TotalVendingSales = totals.TotalVendingSales,
            CardSales = totals.CardSales,
            CashSales = totals.CashSales,
            TotalTransactionCount = facts.TotalTransactionCount,
            CashTransactionCount = facts.CashTransactionCount,
            PendingTransactionCount = facts.PendingTransactionCount,
            RefundedTransactionCount = facts.RefundedTransactionCount,
            DeclinedOrCancelledTransactionCount = facts.DeclinedOrCancelledTransactionCount,
            UnknownStatusTransactionCount = facts.UnknownStatusTransactionCount,
            CardTransactionSales = totals.CardTransactionSales,
            NayaxReportedGrossCardSales = totals.NayaxReportedGrossCardSales,
            NayaxReportedCardTransactionCount = totals.NayaxReportedCardTransactionCount,
            GrossDifference = totals.GrossDifference,
            GrossStatus = totals.GrossStatus,
            ProcessingFeesExGst = totals.ProcessingFeesExGst,
            FeeGst = totals.FeeGst,
            OtherFees = totals.OtherFees,
            Adjustments = totals.Adjustments,
            ExpectedNetReimbursement = totals.ExpectedNetReimbursement,
            ActualNetReimbursement = actualNet,
            SettlementDifference = totals.SettlementDifference,
            SettlementStatus = totals.SettlementStatus,
            Status = totals.Status,
            PeriodRows = periodRows,
            Totals = totals
        };
    }

    private static ReconciliationPeriodDto BuildPeriodRow(ReconciliationPeriodFacts period, bool isMachineFiltered, decimal tolerance)
    {
        var result = ReconciliationPeriodPolicy.Calculate(new ReconciliationPeriodInputs(
            CardSales: period.CardSales,
            CardTransactionCount: period.CardTransactionCount,
            ReportedGross: period.ReportedGross,
            ReportedCount: period.ReportedCount,
            ProcessingFeesExGst: period.ProcessingFeesExGst,
            FeeGst: period.FeeGst,
            OtherFees: period.OtherFees,
            Adjustments: Adjustments,
            ActualNetReimbursement: period.ActualNetReimbursement,
            HasImported: period.HasImported,
            Warning: period.Warning,
            Tolerance: tolerance));

        var notes = new List<string>();
        if (!period.HasImported)
            notes.Add("No imported reimbursement row matched the requested start and end dates.");
        if (isMachineFiltered)
            notes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        if (period.PaymentDetailMissing)
            notes.Add("Imported card payment detail was unavailable; device or reimbursement gross was used as the card gross.");
        notes.Add("Adjustments are unsupported by the imported reimbursement model and are treated as zero.");
        var quality = ReportingQuality.Quality(period.HasImported, period.HasGstClassification, false, string.Join(" ", notes));

        return new ReconciliationPeriodDto(
            period.From, period.To, period.TotalVendingSales, period.CardSales, period.CashSales, period.CardSales,
            period.ReportedGross, period.CardTransactionCount, period.ReportedCount, result.CountDifference,
            result.GrossDifference, result.GrossStatus, period.ProcessingFeesExGst, period.FeeGst, period.OtherFees,
            Adjustments, result.ExpectedNetReimbursement, period.ActualNetReimbursement, result.SettlementDifference,
            result.SettlementStatus, result.OverallStatus, period.PayoutDate, quality)
        {
            TotalTransactionCount = period.TotalTransactionCount,
            CashTransactionCount = period.CashTransactionCount
        };
    }

    private static ReconciliationTotalsDto BuildTotals(
        ReconciliationReportFacts facts, IReadOnlyList<ReconciliationPeriodDto> periodRows, decimal tolerance)
    {
        var reportedGross = periodRows.Sum(x => x.NayaxReportedGrossCardSales);
        var reportedCount = periodRows.Sum(x => x.NayaxReportedCardTransactionCount);
        var aggregateWarning = periodRows.Any(x => x.GrossStatus == "Warning" || x.SettlementStatus == "Warning") ||
            facts.PendingTransactionCount > 0;

        var result = ReconciliationPeriodPolicy.Calculate(new ReconciliationPeriodInputs(
            CardSales: facts.CardSales,
            CardTransactionCount: facts.CardTransactionCount,
            ReportedGross: reportedGross,
            ReportedCount: reportedCount,
            ProcessingFeesExGst: periodRows.Sum(x => x.ProcessingFeesExGst),
            FeeGst: periodRows.Sum(x => x.FeeGst),
            OtherFees: periodRows.Sum(x => x.OtherFees),
            Adjustments: Adjustments,
            ActualNetReimbursement: periodRows.Sum(x => x.ActualNetReimbursement),
            HasImported: facts.HasMatchedReimbursement,
            Warning: aggregateWarning,
            Tolerance: tolerance));

        return new ReconciliationTotalsDto(
            facts.TotalVendingSales, facts.CardSales, facts.CashSales, facts.CardSales, reportedGross,
            facts.CardTransactionCount, reportedCount, result.CountDifference, result.GrossDifference,
            periodRows.Sum(x => x.ProcessingFeesExGst), periodRows.Sum(x => x.FeeGst), periodRows.Sum(x => x.OtherFees),
            Adjustments, result.ExpectedNetReimbursement, periodRows.Sum(x => x.ActualNetReimbursement),
            result.SettlementDifference, result.GrossStatus, result.SettlementStatus, result.OverallStatus)
        {
            TotalTransactionCount = facts.TotalTransactionCount,
            CashTransactionCount = facts.CashTransactionCount
        };
    }

    private static void AddStatusQualityNotes(List<string> notes, ReconciliationReportFacts facts)
    {
        if (facts.PendingTransactionCount > 0)
            notes.Add($"{facts.PendingTransactionCount} pending Nayax transaction(s) are excluded from completed sales.");
        if (facts.RefundedTransactionCount > 0)
            notes.Add($"{facts.RefundedTransactionCount} refunded Nayax transaction(s) are excluded from completed sales.");
        if (facts.DeclinedOrCancelledTransactionCount > 0)
            notes.Add($"{facts.DeclinedOrCancelledTransactionCount} cancelled or declined Nayax transaction(s) are excluded from completed sales.");
        if (facts.UnknownStatusTransactionCount > 0)
            notes.Add($"{facts.UnknownStatusTransactionCount} Nayax transaction(s) have unrecognised status IDs.");
        if (facts.NullStatusTransactionCount > 0)
            notes.Add($"{facts.NullStatusTransactionCount} Nayax transaction(s) have no status ID and are excluded from completed sales.");
    }
}
