using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Daily;

public class GetDailyReportTests
{
    private static void AssertNoteContains(DailyReportDto report, string text) =>
        Assert.Contains(text, string.Join(" ", report.DataQuality.Notes!));

    [Fact]
    public async Task Complete_day_returns_gross_profit_margin_and_reconciled_status()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay();
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(40m, row.CostOfGoods);
        Assert.Equal(60m, row.GrossProfit);
        Assert.Equal(60m, row.GrossMarginPercent);
        Assert.Equal(10m, row.AverageSale);
        Assert.True(row.IsReconciled);
        Assert.Equal("Reconciled", row.ReconciliationStatus);
        Assert.Equal("Actual", row.NayaxFeeSource);

        Assert.Equal(100m, report.Totals!.GrossSales);
        Assert.Equal(80m, report.Totals.CardSales);
        Assert.Equal(20m, report.Totals.CashSales);
        Assert.Equal(40m, report.Totals.CostOfGoods);
        Assert.Equal(60m, report.Totals.GrossProfit);
        Assert.Equal(4m, report.Totals.NayaxFeesExGst);
        Assert.Equal(4.4m, report.Totals.NayaxFeesIncludingGst);
        Assert.Equal(80m, report.Totals.ImportedReimbursement);
        Assert.Equal(78m, report.Totals.NetReimbursement);
        Assert.Equal(4, report.DataQuality.Notes!.Count);
    }

    [Fact]
    public async Task Incomplete_cogs_reports_null_cost_and_profit_with_a_data_quality_note_and_warning_status()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(
            isCogsComplete: false, uncostedTransactionCount: 1, uncostedSalesAmount: 10m, hasDataQualityWarning: true);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.CostOfGoods);
        Assert.Null(row.GrossProfit);
        Assert.Null(row.GrossMarginPercent);
        Assert.False(row.IsCogsComplete);
        Assert.True(row.IsReconciled);
        Assert.Equal("Warning", row.ReconciliationStatus);

        Assert.Null(report.Totals!.CostOfGoods);
        Assert.Null(report.Totals.GrossProfit);
        Assert.Equal(1, report.Totals.UncostedTransactionCount);
        Assert.Equal(10m, report.Totals.UncostedSalesAmount);
        AssertNoteContains(report, "One or more completed sales have no persisted COGS; profit is incomplete.");
    }

    [Fact]
    public async Task Missing_nayax_fee_rates_add_a_provisional_fee_note()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(
            processingFees: new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null, MissingRateTransactionCount: 3));
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        AssertNoteContains(report, "3 card transaction(s) have no effective Nayax processing fee rate; fee totals are provisional.");
    }

    [Fact]
    public async Task Machine_filter_limited_fees_add_an_account_level_note()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(importedFeesMachineFilterLimited: true);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), MachineId: 10), CancellationToken.None);

        AssertNoteContains(report, "Imported fees are account-level amounts and are not allocated to a selected machine.");
    }

    [Fact]
    public async Task Period_only_imported_data_adds_an_allocation_note()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(hasAnyPeriodOnlyImportedData: true);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        AssertNoteContains(report, "Some reimbursements cover a period longer than one day and are not allocated to daily rows.");
    }

    [Fact]
    public async Task Non_completed_status_counts_are_surfaced_as_notes()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(
            pendingTransactionCount: 2, refundedTransactionCount: 1, declinedOrCancelledTransactionCount: 1,
            unknownStatusTransactionCount: 1, nullStatusTransactionCount: 1);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        AssertNoteContains(report, "2 pending Nayax transaction(s) are excluded from completed sales.");
        AssertNoteContains(report, "1 refunded Nayax transaction(s) are excluded from completed sales.");
        AssertNoteContains(report, "1 cancelled or declined Nayax transaction(s) are excluded from completed sales.");
        AssertNoteContains(report, "1 Nayax transaction(s) have unrecognised status IDs.");
        AssertNoteContains(report, "1 Nayax transaction(s) have no status ID and are excluded from completed sales.");
    }

    [Fact]
    public async Task Complete_day_reports_available_cost_and_gst_classification_and_no_status_or_product_claims()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay();
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.False(report.DataQuality.MissingStatus);
        Assert.False(report.DataQuality.HistoricalCostUnavailable);
        Assert.False(report.DataQuality.GstClassificationMissing);
        Assert.True(report.DataQuality.CommissionNotPersisted);
        Assert.False(report.DataQuality.ContainsUnmappedProducts);
    }

    [Fact]
    public async Task Null_or_unknown_status_transactions_set_the_missing_status_flag()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(nullStatusTransactionCount: 1);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.True(report.DataQuality.MissingStatus);
    }

    [Fact]
    public async Task Incomplete_cogs_sets_the_historical_cost_unavailable_flag()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(isCogsComplete: false, uncostedTransactionCount: 1, uncostedSalesAmount: 10m);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.True(report.DataQuality.HistoricalCostUnavailable);
    }

    [Fact]
    public async Task Absent_imported_gst_classification_sets_the_gst_classification_missing_flag()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay() with { ImportedContainsGstClassification = false };
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.True(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Multiple_report_specific_notes_are_kept_as_separate_ordered_entries()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(
            processingFees: new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null, MissingRateTransactionCount: 3),
            isCogsComplete: false, uncostedTransactionCount: 1, uncostedSalesAmount: 10m);
        var useCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Equal(6, report.DataQuality.Notes!.Count);
        Assert.Equal("3 card transaction(s) have no effective Nayax processing fee rate; fee totals are provisional.", report.DataQuality.Notes[4]);
        Assert.Equal("One or more completed sales have no persisted COGS; profit is incomplete.", report.DataQuality.Notes[5]);
    }

    [Fact]
    public async Task A_financial_year_filter_resolves_the_range_passed_to_the_facts_port()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay();
        var provider = new FakeDailyReportFactsProvider(facts);
        var useCase = new GetDailyReport(provider);

        var report = await useCase.Handle(new ReportingFilterDto(FinancialYear: "FY2025-26"), CancellationToken.None);

        Assert.Equal(new DateTime(2025, 7, 1), report.From);
        Assert.Equal(new DateTime(2026, 6, 30), report.To);
        Assert.Equal((new DateTime(2025, 7, 1), new DateTime(2026, 6, 30), (long?)null), provider.LastRequest);
    }
}
