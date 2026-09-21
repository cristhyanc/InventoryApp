using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Reconciliation;

public class GetReconciliationReportTests
{
    private static void AssertNoteContains(ReconciliationReportDto report, string text) =>
        Assert.Contains(text, string.Join(" ", report.DataQuality.Notes!));

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
        AssertNoteContains(report, "No imported reimbursement row matched the requested start and end dates.");
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
    public async Task Non_completed_status_counts_are_surfaced_as_notes_and_exposed_on_the_report()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(
            pendingTransactionCount: 2, refundedTransactionCount: 1, declinedOrCancelledTransactionCount: 1,
            unknownStatusTransactionCount: 1, nullStatusTransactionCount: 1);
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Equal(2, report.PendingTransactionCount);
        Assert.Equal(1, report.RefundedTransactionCount);
        Assert.Equal(1, report.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, report.UnknownStatusTransactionCount);
        AssertNoteContains(report, "2 pending Nayax transaction(s) are excluded from completed sales.");
        AssertNoteContains(report, "1 refunded Nayax transaction(s) are excluded from completed sales.");
        AssertNoteContains(report, "1 cancelled or declined Nayax transaction(s) are excluded from completed sales.");
        AssertNoteContains(report, "1 Nayax transaction(s) have unrecognised status IDs.");
        AssertNoteContains(report, "1 Nayax transaction(s) have no status ID and are excluded from completed sales.");
    }

    [Fact]
    public async Task Adjustments_note_is_always_present_because_the_imported_model_does_not_support_them()
    {
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod();
        var useCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Equal(0m, report.Adjustments);
        Assert.False(report.AdjustmentsSupported);
        AssertNoteContains(report, "Adjustments are unsupported by the imported reimbursement model and are treated as zero.");
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
