using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Reconciliation;

public class GetReconciliationReportTests
{
    private static void AssertNoteContains(ReconciliationReportDto report, string text) =>
        Assert.Contains(text, string.Join(" ", report.DataQuality.Notes!));

    private static void AssertNoNoteContains(ReconciliationReportDto report, string text) =>
        Assert.DoesNotContain(text, string.Join(" ", report.DataQuality.Notes!));

    private static ReconciliationReportFacts Facts(
        IReadOnlyList<ReconciliationPeriodFacts> periods,
        bool hasMatchedReimbursement = true,
        int unknownPaymentTransactionCount = 0,
        int pendingTransactionCount = 0,
        int refundedTransactionCount = 0,
        int declinedOrCancelledTransactionCount = 0,
        int unknownStatusTransactionCount = 0,
        int nullStatusTransactionCount = 0,
        bool isMachineFiltered = false) =>
        new(periods, hasMatchedReimbursement,
            TotalVendingSales: periods.Sum(x => x.TotalVendingSales),
            CardSales: periods.Sum(x => x.CardSales),
            CashSales: periods.Sum(x => x.CashSales),
            TotalTransactionCount: periods.Sum(x => x.TotalTransactionCount),
            CardTransactionCount: periods.Sum(x => x.CardTransactionCount),
            CashTransactionCount: periods.Sum(x => x.CashTransactionCount),
            unknownPaymentTransactionCount, pendingTransactionCount, refundedTransactionCount,
            declinedOrCancelledTransactionCount, unknownStatusTransactionCount, nullStatusTransactionCount,
            isMachineFiltered);

    [Fact]
    public async Task Matched_period_within_tolerance_is_reconciled_and_totals_mirror_the_single_period()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod();
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.True(report.IsMatch);
        Assert.Equal("Reconciled", report.GrossStatus);
        Assert.Equal("Reconciled", report.SettlementStatus);
        Assert.Equal("Reconciled", report.Status);
        Assert.Equal(0m, report.GrossDifference);
        var row = Assert.Single(report.PeriodRows);
        Assert.Equal(80m, row.NayaxReportedGrossCardSales);
        Assert.Equal(80m, report.Totals!.NayaxReportedGrossCardSales);
    }

    [Fact]
    public async Task No_matching_reimbursement_falls_back_to_a_pending_period_covering_the_requested_range()
    {
        var period = FakeReconciliationReportFactsProvider.Period(hasImported: false, reportedGross: 0m, reportedCount: 0,
            processingFeesExGst: 0m, feeGst: 0m, otherFees: 0m, actualNetReimbursement: 0m);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period, hasMatchedReimbursement: false);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        Assert.False(report.IsMatch);
        Assert.Equal("Pending", report.GrossStatus);
        Assert.Equal("Pending", report.SettlementStatus);
        Assert.Equal(0m, report.ImportedReimbursement);
        AssertNoteContains(report, "No imported Nayax reimbursement period falls entirely inside");
    }

    [Fact]
    public async Task Multiple_matched_periods_keep_their_own_rows_and_aggregate_into_one_totals_row()
    {
        var periodA = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 15),
            totalVendingSales: 60m, cardSales: 50m, cashSales: 10m, cardTransactionCount: 5,
            totalTransactionCount: 6, cashTransactionCount: 1, reportedGross: 50m, reportedCount: 5,
            processingFeesExGst: 2.5m, feeGst: 0.25m, actualNetReimbursement: 47.25m);
        var periodB = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 16), to: new DateTime(2025, 8, 31),
            totalVendingSales: 40m, cardSales: 30m, cashSales: 10m, cardTransactionCount: 3,
            totalTransactionCount: 4, cashTransactionCount: 1, reportedGross: 30m, reportedCount: 3,
            processingFeesExGst: 1.5m, feeGst: 0.15m, actualNetReimbursement: 28.35m);
        var facts = new ReconciliationReportFacts(
            new List<ReconciliationPeriodFacts> { periodA, periodB }, true,
            TotalVendingSales: 100m, CardSales: 80m, CashSales: 20m,
            TotalTransactionCount: 10, CardTransactionCount: 8, CashTransactionCount: 2,
            UnknownPaymentTransactionCount: 0, PendingTransactionCount: 0, RefundedTransactionCount: 0,
            DeclinedOrCancelledTransactionCount: 0, UnknownStatusTransactionCount: 0, NullStatusTransactionCount: 0,
            IsMachineFiltered: false);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        Assert.Equal(2, report.PeriodRows.Count);
        Assert.Equal(80m, report.Totals!.NayaxReportedGrossCardSales);
        Assert.Equal(8, report.Totals.NayaxReportedCardTransactionCount);
        Assert.Equal(4m, report.Totals.ProcessingFeesExGst);
        Assert.Equal(0.4m, report.Totals.FeeGst);
        Assert.Equal(75.6m, report.Totals.ActualNetReimbursement);
        Assert.True(report.IsMatch);
    }

    [Fact]
    public async Task Machine_filter_adds_an_account_level_fee_note()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(isMachineFiltered: true);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), MachineId: 10), 0.01m, CancellationToken.None);

        AssertNoteContains(report, "Imported fees are account-level amounts and are not allocated to a selected machine.");
    }

    [Fact]
    public async Task Unknown_payment_method_transactions_add_a_note()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(unknownPaymentTransactionCount: 2);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        AssertNoteContains(report, "One or more transactions have an unknown payment method.");
    }

    [Fact]
    public async Task Status_exclusion_counts_are_exposed_on_the_report_and_only_real_problems_warn()
    {
        var period = FakeReconciliationReportFactsProvider.Period(
            pendingTransactionCount: 2, refundedTransactionCount: 1, declinedOrCancelledTransactionCount: 17,
            unknownStatusTransactionCount: 1, missingStatusTransactionCount: 1);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period,
            pendingTransactionCount: 2, refundedTransactionCount: 1, declinedOrCancelledTransactionCount: 17,
            unknownStatusTransactionCount: 1, nullStatusTransactionCount: 1);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Equal(2, report.PendingTransactionCount);
        Assert.Equal(1, report.RefundedTransactionCount);
        Assert.Equal(17, report.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, report.UnknownStatusTransactionCount);
        Assert.Equal(1, report.MissingStatusTransactionCount);
        // A refunded, cancelled or declined transaction is an ordinary Nayax payment outcome, not a
        // problem to repair: it is reported as a count, never as a warning.
        AssertNoNoteContains(report, "refunded");
        AssertNoNoteContains(report, "cancelled or declined");
        // A pending transaction is not final, and that is why the aggregate reconciliation status
        // stays a warning, so it keeps a note that says exactly that.
        AssertNoteContains(report, "2 pending Nayax transaction(s) are not final sales");
        AssertNoteContains(report, "1 Nayax transaction(s) have an unrecognised status ID and are excluded from completed sales.");
        AssertNoteContains(report, "1 Nayax transaction(s) have no status ID and are excluded from completed sales.");
    }

    [Fact]
    public async Task Declined_or_cancelled_transactions_alone_leave_the_report_without_any_data_quality_note()
    {
        var period = FakeReconciliationReportFactsProvider.Period(declinedOrCancelledTransactionCount: 17);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period, declinedOrCancelledTransactionCount: 17);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Empty(report.DataQuality.Notes!);
        Assert.Equal(17, report.DeclinedOrCancelledTransactionCount);
        Assert.Equal(17, Assert.Single(report.PeriodRows).DeclinedOrCancelledTransactionCount);
    }

    [Fact]
    public async Task A_clean_reconciliation_claims_no_missing_status_cogs_commission_or_sales_gst()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod();
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Empty(report.DataQuality.Notes!);
        Assert.Empty(Assert.Single(report.PeriodRows).DataQuality.Notes!);
        // This report reconciles card sales against an imported reimbursement: it calculates no
        // COGS, no commission and no GST on sales, so it must not claim any of them is missing.
        Assert.False(report.DataQuality.MissingStatus);
        Assert.False(report.DataQuality.HistoricalCostUnavailable);
        Assert.False(report.DataQuality.CommissionNotPersisted);
        Assert.False(report.DataQuality.ContainsUnmappedProducts);
        Assert.False(report.PeriodRows[0].DataQuality.MissingStatus);
        Assert.False(report.PeriodRows[0].DataQuality.HistoricalCostUnavailable);
        Assert.False(report.PeriodRows[0].DataQuality.CommissionNotPersisted);
    }

    [Fact]
    public async Task Adjustments_stay_an_unsupported_zero_assumption_without_a_permanent_data_quality_note()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod();
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Equal(0m, report.Adjustments);
        Assert.False(report.AdjustmentsSupported);
        Assert.False(Assert.Single(report.PeriodRows).AdjustmentsSupported);
        // The assumption is presented beside the Adjustments figure and in the export, not as a
        // permanent data-quality warning about the requested period.
        AssertNoNoteContains(report, "Adjustments are unsupported");
        Assert.Equal(
            "Reimbursement adjustments are not imported; this calculation assumes $0.00.",
            GetReconciliationReport.AdjustmentsAssumption);
    }

    [Fact]
    public async Task Missing_reimbursement_note_is_actionable_and_names_the_requested_range()
    {
        var period = FakeReconciliationReportFactsProvider.Period(hasImported: false, reportedGross: 0m, reportedCount: 0,
            processingFeesExGst: 0m, feeGst: 0m, otherFees: 0m, actualNetReimbursement: 0m, hasGstClassification: false,
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 31));
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period, hasMatchedReimbursement: false);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        // The matching rule requires a reimbursement period to fall entirely inside the requested
        // range, so the note must not imply that no reimbursement has been imported at all.
        AssertNoteContains(report, "No imported Nayax reimbursement period falls entirely inside 2025-08-01 to 2025-08-31");
        AssertNoteContains(report, "cannot be compared");
        AssertNoteContains(report, "Import the reimbursement that covers these dates, or request the period an imported reimbursement covers.");
        Assert.False(report.IsMatch);
    }

    [Fact]
    public async Task Imported_fee_rows_without_a_gst_percentage_explain_the_fee_gst_source_instead_of_sales_gst()
    {
        var period = FakeReconciliationReportFactsProvider.Period(hasGstClassification: false, processingFeesExGst: 4m, feeGst: 0.4m);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        AssertNoteContains(report, "do not state a GST percentage");
        AssertNoNoteContains(report, "indicative");
        Assert.True(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task A_period_without_imported_fees_does_not_warn_about_a_missing_fee_gst_percentage()
    {
        var period = FakeReconciliationReportFactsProvider.Period(hasGstClassification: false,
            processingFeesExGst: 0m, feeGst: 0m, otherFees: 0m);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        AssertNoNoteContains(report, "GST percentage");
        Assert.Empty(report.DataQuality.Notes!);
    }

    [Fact]
    public async Task Period_missing_status_flags_and_notes_use_each_periods_own_counts()
    {
        var clean = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 15));
        var affected = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 16), to: new DateTime(2025, 8, 31),
            missingStatusTransactionCount: 3, unknownStatusTransactionCount: 2, pendingTransactionCount: 1);
        var facts = Facts(new[] { clean, affected }, pendingTransactionCount: 1,
            unknownStatusTransactionCount: 2, nullStatusTransactionCount: 3);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        // The aggregate counts are never copied onto a period: the first period holds none of them.
        Assert.False(report.PeriodRows[0].DataQuality.MissingStatus);
        Assert.Empty(report.PeriodRows[0].DataQuality.Notes!);
        Assert.Equal(0, report.PeriodRows[0].MissingStatusTransactionCount);
        Assert.True(report.PeriodRows[1].DataQuality.MissingStatus);
        Assert.Equal(3, report.PeriodRows[1].MissingStatusTransactionCount);
        Assert.Equal(2, report.PeriodRows[1].UnknownStatusTransactionCount);
        Assert.Equal(1, report.PeriodRows[1].PendingTransactionCount);
        var notes = string.Join(" ", report.PeriodRows[1].DataQuality.Notes!);
        Assert.Contains("3 Nayax transaction(s) have no status ID", notes);
        Assert.Contains("2 Nayax transaction(s) have an unrecognised status ID", notes);
        Assert.True(report.DataQuality.MissingStatus);
    }

    [Fact]
    public async Task A_payment_detail_fallback_in_one_period_is_reported_at_both_levels_with_the_affected_period()
    {
        var clean = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 15));
        var fallback = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 16), to: new DateTime(2025, 8, 31), paymentDetailMissing: true);
        var facts = Facts(new[] { clean, fallback });
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        AssertNoteContains(report, "Imported card payment detail was unavailable for 2025-08-16 to 2025-08-31");
        Assert.Empty(report.PeriodRows[0].DataQuality.Notes!);
        Assert.Contains("Imported card payment detail was unavailable",
            string.Join(" ", report.PeriodRows[1].DataQuality.Notes!));
    }

    [Fact]
    public async Task Period_with_gst_classification_reports_the_flag_as_not_missing()
    {
        var period = FakeReconciliationReportFactsProvider.Period(hasGstClassification: true);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        var row = Assert.Single(report.PeriodRows);
        Assert.False(row.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Period_without_gst_classification_reports_the_flag_as_missing()
    {
        var period = FakeReconciliationReportFactsProvider.Period(hasGstClassification: false);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        var row = Assert.Single(report.PeriodRows);
        Assert.True(row.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Totals_gst_classification_missing_is_false_when_every_period_has_classification()
    {
        var periodA = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 15), hasGstClassification: true);
        var periodB = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 16), to: new DateTime(2025, 8, 31), hasGstClassification: true);
        var facts = new ReconciliationReportFacts(
            new List<ReconciliationPeriodFacts> { periodA, periodB }, true,
            TotalVendingSales: 100m, CardSales: 80m, CashSales: 20m,
            TotalTransactionCount: 10, CardTransactionCount: 8, CashTransactionCount: 2,
            UnknownPaymentTransactionCount: 0, PendingTransactionCount: 0, RefundedTransactionCount: 0,
            DeclinedOrCancelledTransactionCount: 0, UnknownStatusTransactionCount: 0, NullStatusTransactionCount: 0,
            IsMachineFiltered: false);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        Assert.False(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Totals_gst_classification_missing_is_true_when_any_period_lacks_classification()
    {
        var periodA = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 15), hasGstClassification: true);
        var periodB = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 16), to: new DateTime(2025, 8, 31), hasGstClassification: false);
        var facts = new ReconciliationReportFacts(
            new List<ReconciliationPeriodFacts> { periodA, periodB }, true,
            TotalVendingSales: 100m, CardSales: 80m, CashSales: 20m,
            TotalTransactionCount: 10, CardTransactionCount: 8, CashTransactionCount: 2,
            UnknownPaymentTransactionCount: 0, PendingTransactionCount: 0, RefundedTransactionCount: 0,
            DeclinedOrCancelledTransactionCount: 0, UnknownStatusTransactionCount: 0, NullStatusTransactionCount: 0,
            IsMachineFiltered: false);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        Assert.True(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Totals_gst_classification_missing_does_not_depend_on_per_period_data_quality_dtos()
    {
        // Both periods individually have GST classification, so each period row's own
        // DataQuality.GstClassificationMissing is false. If the totals flag were (incorrectly)
        // derived from periodRows[...].DataQuality again, "any period row reports GST classification
        // present" would also evaluate true here and could be wired in with inverted polarity. The
        // fix instead reads facts.Periods directly, so the totals flag reflects the true aggregate
        // (every period has classification -> not missing) regardless of how the per-period DTOs
        // were built.
        var periodA = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 1), to: new DateTime(2025, 8, 15), hasGstClassification: true);
        var periodB = FakeReconciliationReportFactsProvider.Period(
            from: new DateTime(2025, 8, 16), to: new DateTime(2025, 8, 31), hasGstClassification: true);
        var facts = new ReconciliationReportFacts(
            new List<ReconciliationPeriodFacts> { periodA, periodB }, true,
            TotalVendingSales: 100m, CardSales: 80m, CashSales: 20m,
            TotalTransactionCount: 10, CardTransactionCount: 8, CashTransactionCount: 2,
            UnknownPaymentTransactionCount: 0, PendingTransactionCount: 0, RefundedTransactionCount: 0,
            DeclinedOrCancelledTransactionCount: 0, UnknownStatusTransactionCount: 0, NullStatusTransactionCount: 0,
            IsMachineFiltered: false);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m, CancellationToken.None);

        Assert.All(report.PeriodRows, row => Assert.False(row.DataQuality.GstClassificationMissing));
        Assert.False(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Totals_missing_status_flag_reflects_null_and_unknown_status_counts()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(nullStatusTransactionCount: 1);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.True(report.DataQuality.MissingStatus);
    }

    [Fact]
    public async Task A_financial_year_filter_resolves_the_range_passed_to_the_facts_port()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod();
        var provider = new FakeReconciliationReportFactsProvider(facts);
        var useCase = new GetReconciliationReport(provider);

        var report = await useCase.Handle(new ReportingFilterDto(FinancialYear: "FY2025-26"), 0.01m, CancellationToken.None);

        Assert.Equal(new DateTime(2025, 7, 1), report.From);
        Assert.Equal(new DateTime(2026, 6, 30), report.To);
        Assert.Equal((new DateTime(2025, 7, 1), new DateTime(2026, 6, 30), (long?)null), provider.LastRequest);
    }
}
