using System.Globalization;
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

    /// <summary>
    /// The one authoritative statement of the adjustments assumption (issue #477). The imported
    /// reimbursement model carries no adjustment facts, so every adjustment amount in this report is
    /// an assumed zero, not a verified one. It is presented beside the Adjustments/expected-net
    /// figures and in the CSV/XLSX export rather than as a data-quality warning, because it describes
    /// the calculation rather than a problem with the requested period.
    /// </summary>
    public const string AdjustmentsAssumption =
        "Reimbursement adjustments are not imported; this calculation assumes $0.00.";

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

        // Conditional, scope-derived diagnostics (issue #477, reusing issue #476's shared helper):
        // every note below comes from a fact about the requested range and machine scope. A
        // reconciliation of card sales against an imported reimbursement calculates no COGS, no
        // commission and no GST on sales, so it never claims any of those is missing, and a refunded
        // or cancelled/declined transaction is an ordinary payment outcome reported as a count
        // instead of a warning. The report's calculation methodology, including this report's
        // assumed-zero adjustments, is presented beside the figures it explains.
        var qualityNotes = new List<string>();
        if (!facts.HasMatchedReimbursement)
            qualityNotes.Add(MissingReimbursementNote(range.From, range.ToDate));
        if (facts.UnknownPaymentTransactionCount > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        if (facts.NullStatusTransactionCount > 0)
            qualityNotes.Add(MissingStatusNote(facts.NullStatusTransactionCount));
        if (facts.UnknownStatusTransactionCount > 0)
            qualityNotes.Add(UnrecognisedStatusNote(facts.UnknownStatusTransactionCount));
        if (facts.PendingTransactionCount > 0)
            qualityNotes.Add(PendingNote(facts.PendingTransactionCount));
        if (facts.IsMachineFiltered)
            qualityNotes.Add(MachineFilteredFeeNote);
        var paymentDetailPeriods = facts.Periods.Where(period => period.PaymentDetailMissing).ToList();
        if (paymentDetailPeriods.Count > 0)
            qualityNotes.Add($"Imported card payment detail was unavailable for {PeriodScope(paymentDetailPeriods)}; " +
                "the device or reimbursement gross was used as the Nayax card gross.");
        var unstatedFeeGstPeriods = facts.Periods.Where(HasUnstatedFeeGstPercentage).ToList();
        if (unstatedFeeGstPeriods.Count > 0)
            qualityNotes.Add($"Imported fee rows for {PeriodScope(unstatedFeeGstPeriods)} do not state a GST percentage; " +
                "fee GST is taken from the imported GST-inclusive and GST-exclusive fee amounts, and is $0.00 where only one of them was imported.");
        var quality = ReportingQuality.Conditional(
            missingStatus: facts.NullStatusTransactionCount > 0 || facts.UnknownStatusTransactionCount > 0,
            historicalCostUnavailable: false,
            gstClassificationMissing: facts.Periods.Any(period => !period.HasGstClassification),
            commissionNotPersisted: false,
            containsUnmappedProducts: false,
            notes: qualityNotes);

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
            MissingStatusTransactionCount = facts.NullStatusTransactionCount,
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

        // Every period note is derived from this period's own facts (issue #477): the aggregate
        // counts are never copied onto a period, and a period with no detected problem returns an
        // empty note list.
        var notes = new List<string>();
        if (!period.HasImported)
            notes.Add($"No imported Nayax reimbursement covers {PeriodRange(period)}, " +
                "so its recorded card sales cannot be compared with a Nayax payout.");
        if (isMachineFiltered)
            notes.Add(MachineFilteredFeeNote);
        if (period.PaymentDetailMissing)
            notes.Add("Imported card payment detail was unavailable; the device or reimbursement gross was used as the Nayax card gross.");
        if (period.MissingStatusTransactionCount > 0)
            notes.Add(MissingStatusNote(period.MissingStatusTransactionCount));
        if (period.UnknownStatusTransactionCount > 0)
            notes.Add(UnrecognisedStatusNote(period.UnknownStatusTransactionCount));
        if (period.PendingTransactionCount > 0)
            notes.Add(PendingNote(period.PendingTransactionCount));
        if (HasUnstatedFeeGstPercentage(period))
            notes.Add("Imported fee rows for this period do not state a GST percentage; " +
                "fee GST is taken from the imported GST-inclusive and GST-exclusive fee amounts, and is $0.00 where only one of them was imported.");
        var quality = ReportingQuality.Conditional(
            missingStatus: period.MissingStatusTransactionCount > 0 || period.UnknownStatusTransactionCount > 0,
            historicalCostUnavailable: false,
            gstClassificationMissing: !period.HasGstClassification,
            commissionNotPersisted: false,
            containsUnmappedProducts: false,
            notes: notes);

        return new ReconciliationPeriodDto(
            period.From, period.To, period.TotalVendingSales, period.CardSales, period.CashSales, period.CardSales,
            period.ReportedGross, period.CardTransactionCount, period.ReportedCount, result.CountDifference,
            result.GrossDifference, result.GrossStatus, period.ProcessingFeesExGst, period.FeeGst, period.OtherFees,
            Adjustments, result.ExpectedNetReimbursement, period.ActualNetReimbursement, result.SettlementDifference,
            result.SettlementStatus, result.OverallStatus, period.PayoutDate, quality)
        {
            TotalTransactionCount = period.TotalTransactionCount,
            CashTransactionCount = period.CashTransactionCount,
            PendingTransactionCount = period.PendingTransactionCount,
            RefundedTransactionCount = period.RefundedTransactionCount,
            DeclinedOrCancelledTransactionCount = period.DeclinedOrCancelledTransactionCount,
            UnknownStatusTransactionCount = period.UnknownStatusTransactionCount,
            MissingStatusTransactionCount = period.MissingStatusTransactionCount
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

    private const string MachineFilteredFeeNote =
        "Imported fees are account-level amounts and are not allocated to a selected machine.";

    /// <summary>
    /// A reimbursement matches only when its own coverage period falls entirely inside the requested
    /// range, so an unmatched range says nothing about whether reimbursements have been imported for
    /// other dates. The note states that, and what the reader can do about it.
    /// </summary>
    private static string MissingReimbursementNote(DateTime from, DateTime to) =>
        $"No imported Nayax reimbursement period falls entirely inside {Day(from)} to {Day(to)}, " +
        "so recorded card sales cannot be compared with a Nayax payout. " +
        "Import the reimbursement that covers these dates, or request the period an imported reimbursement covers.";

    private static string MissingStatusNote(int count) =>
        $"{count} Nayax transaction(s) have no status ID and are excluded from completed sales.";

    private static string UnrecognisedStatusNote(int count) =>
        $"{count} Nayax transaction(s) have an unrecognised status ID and are excluded from completed sales.";

    // Pending rows are not a data error, but they are not final either, and they are why an
    // otherwise matching period still reports a warning - so the note explains that significance
    // rather than asking for a repair. Refunded and cancelled/declined rows are ordinary outcomes
    // and are reported only as counts.
    private static string PendingNote(int count) =>
        $"{count} pending Nayax transaction(s) are not final sales and are excluded from card sales; " +
        "reconciliation stays provisional until they settle.";

    /// <summary>
    /// True when this period's fee GST could not be read from an imported GST percentage and the
    /// period actually holds fee amounts to explain. A period with no imported fees has no fee GST
    /// figure, so it is not described as a limitation.
    /// </summary>
    private static bool HasUnstatedFeeGstPercentage(ReconciliationPeriodFacts period) =>
        !period.HasGstClassification &&
        (period.ProcessingFeesExGst != 0m || period.FeeGst != 0m || period.OtherFees != 0m);

    private static string PeriodScope(IReadOnlyList<ReconciliationPeriodFacts> periods) =>
        string.Join(", ", periods.Select(PeriodRange));

    private static string PeriodRange(ReconciliationPeriodFacts period) => $"{Day(period.From)} to {Day(period.To)}";

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
