using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Bookkeeping;

public class GetBookkeepingReportTests
{
    private static void AssertNoteContains(BookkeepingReportDto report, string text) =>
        Assert.Contains(text, string.Join(" ", report.DataQuality.Notes!));


    [Fact]
    public async Task Whole_business_complete_scope_returns_net_profit_and_no_direct_profit()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete();
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(100m, report.Sales);
        Assert.Equal(40m, report.CostOfGoods);
        Assert.Equal(60m, report.GrossProfit);
        Assert.NotNull(report.NetProfit);
        Assert.Null(report.DirectProfit);
        Assert.Equal("FY2025-26", report.FinancialYear);
        Assert.Empty(report.DataQuality.Notes!);
        Assert.False(report.DataQuality.MissingStatus);
        Assert.False(report.DataQuality.HistoricalCostUnavailable);
        Assert.False(report.DataQuality.GstClassificationMissing);
        Assert.False(report.DataQuality.CommissionNotPersisted);
        Assert.False(report.DataQuality.ContainsUnmappedProducts);
    }

    [Fact]
    public async Task Absent_imported_gst_classification_sets_the_gst_classification_missing_flag()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with { ImportedContainsGstClassification = false };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.True(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Machine_filter_returns_direct_profit_and_a_net_profit_unavailable_note()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete();
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31), MachineId: 10), CancellationToken.None);

        Assert.NotNull(report.DirectProfit);
        Assert.Null(report.NetProfit);
        AssertNoteContains(report, "Net profit is unavailable for a machine-filtered report");
    }

    [Fact]
    public async Task Incomplete_cogs_reports_null_cost_and_profit_with_a_data_quality_note()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            IsCogsComplete = false,
            UncostedTransactionCount = 1,
            UncostedSalesAmount = 10m
        };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Null(report.CostOfGoods);
        Assert.Null(report.GrossProfit);
        Assert.Null(report.NetProfit);
        Assert.False(report.IsCogsComplete);
        Assert.Equal(1, report.UncostedTransactionCount);
        Assert.Equal(10m, report.UncostedSalesAmount);
        Assert.True(report.DataQuality.HistoricalCostUnavailable);
        AssertNoteContains(report, "1 completed sale(s) totalling 10.00 have no persisted COGS; profit is incomplete.");
        Assert.Single(report.DataQuality.Notes!);
    }

    [Fact]
    public async Task A_clean_period_reports_no_data_quality_notes_at_all()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete();
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Empty(report.DataQuality.Notes!);
        Assert.False(report.DataQuality.MissingStatus);
        Assert.False(report.DataQuality.HistoricalCostUnavailable);
        Assert.Equal(0, report.MissingStatusTransactionCount);
    }

    [Fact]
    public async Task Rows_with_no_status_id_are_reported_with_their_count_and_set_the_missing_status_flag()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with { MissingStatusTransactionCount = 3 };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.True(report.DataQuality.MissingStatus);
        Assert.Equal(3, report.MissingStatusTransactionCount);
        AssertNoteContains(report, "3 Nayax transaction(s) have no status ID and are excluded from completed sales.");
        Assert.Single(report.DataQuality.Notes!);
    }

    [Fact]
    public async Task Unrecognised_status_ids_are_reported_separately_from_rows_with_no_status_id()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with { UnknownStatusTransactionCount = 2 };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.True(report.DataQuality.MissingStatus);
        Assert.Equal(2, report.UnknownStatusTransactionCount);
        Assert.Equal(0, report.MissingStatusTransactionCount);
        AssertNoteContains(report, "2 Nayax transaction(s) have an unrecognised status ID and are excluded from completed sales.");
        Assert.Single(report.DataQuality.Notes!);
    }

    [Fact]
    public async Task Pending_refunded_and_cancelled_rows_are_counted_but_are_not_a_data_quality_problem()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            PendingTransactionCount = 4,
            RefundedTransactionCount = 1,
            DeclinedOrCancelledTransactionCount = 7
        };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(4, report.PendingTransactionCount);
        Assert.Equal(1, report.RefundedTransactionCount);
        Assert.Equal(7, report.DeclinedOrCancelledTransactionCount);
        Assert.False(report.DataQuality.MissingStatus);
        Assert.Empty(report.DataQuality.Notes!);
    }

    [Fact]
    public async Task Financial_totals_are_unchanged_by_excluded_status_rows_in_the_same_scope()
    {
        var clean = FakeBookkeepingReportFactsProvider.Complete();
        var withExcludedRows = clean with
        {
            PendingTransactionCount = 2,
            DeclinedOrCancelledTransactionCount = 3,
            MissingStatusTransactionCount = 1,
            UnknownStatusTransactionCount = 1
        };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(clean));
        var diagnosingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(withExcludedRows));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var baseline = await useCase.Handle(filter, CancellationToken.None);
        var diagnosed = await diagnosingUseCase.Handle(filter, CancellationToken.None);

        Assert.Equal(baseline.Sales, diagnosed.Sales);
        Assert.Equal(baseline.CostOfGoods, diagnosed.CostOfGoods);
        Assert.Equal(baseline.GrossProfit, diagnosed.GrossProfit);
        Assert.Equal(baseline.NetProfit, diagnosed.NetProfit);
        Assert.Equal(baseline.GstOnSales, diagnosed.GstOnSales);
        Assert.Equal(baseline.SiteCommission, diagnosed.SiteCommission);
    }

    [Fact]
    public async Task Missing_nayax_fee_rates_add_a_provisional_profit_note()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null, MissingRateTransactionCount: 3)
        };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Null(report.NetProfit);
        AssertNoteContains(report, "3 card transaction(s) have no effective Nayax processing fee rate; profit is unavailable.");
    }

    [Fact]
    public async Task Incomplete_commission_configuration_adds_a_note_and_its_warnings()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            CommissionCompleteForScope = false,
            CommissionIsComplete = false,
            CommissionWarnings = ["Overlapping commission agreements cover one or more sales for the selected machine."]
        };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Null(report.NetProfit);
        Assert.True(report.DataQuality.CommissionNotPersisted);
        AssertNoteContains(report, "Commission configuration is incomplete; net profit is unavailable.");
        AssertNoteContains(report, "Overlapping commission agreements cover one or more sales for the selected machine.");
    }

    [Fact]
    public async Task Unmatched_machine_filter_and_account_level_fees_are_both_surfaced()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            ImportedMachineFilterMatched = false,
            ImportedFeesMachineFilterLimited = true
        };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31), MachineId: 10), CancellationToken.None);

        AssertNoteContains(report, "No reimbursement device row matched the selected machine ID.");
        AssertNoteContains(report, "Imported fees are account-level amounts and are not allocated to a selected machine.");
    }

    [Fact]
    public async Task Unknown_payment_method_transactions_are_surfaced_in_the_notes()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with { UnknownTransactions = 2 };
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        AssertNoteContains(report, "One or more transactions have an unknown payment method.");
    }

    [Fact]
    public async Task Net_settlement_prefers_the_imported_amount_over_the_derived_estimate()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete();
        var provider = new FakeBookkeepingReportFactsProvider(facts);
        var useCase = new GetBookkeepingReport(provider);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(74m, report.NetSettlement);
    }

    [Fact]
    public async Task A_financial_year_filter_resolves_the_range_passed_to_the_facts_port()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete();
        var provider = new FakeBookkeepingReportFactsProvider(facts);
        var useCase = new GetBookkeepingReport(provider);

        var report = await useCase.Handle(new ReportingFilterDto(FinancialYear: "FY2025-26"), CancellationToken.None);

        Assert.Equal(new DateTime(2025, 7, 1), report.From);
        Assert.Equal(new DateTime(2026, 6, 30), report.To);
        Assert.Equal((new DateTime(2025, 7, 1), new DateTime(2026, 6, 30), (long?)null), provider.LastRequest);
    }

    [Fact]
    public async Task CardSales_and_CashSales_and_transaction_counts_pass_through_from_facts()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete(cardSales: 80m, cardTransactions: 8, cashSales: 20m, cashTransactions: 2);
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(80m, report.CardSales);
        Assert.Equal(20m, report.CashSales);
        Assert.Equal(8, report.CardTransactionCount);
        Assert.Equal(2, report.CashTransactionCount);
    }
}
