using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Bookkeeping;

public class GetBookkeepingReportTests
{
    // The use case joins its per-condition notes into a single combined string before appending it
    // to the boilerplate notes, so a note's text may share an array element with other notes.
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
        Assert.Equal(4, report.DataQuality.Notes!.Count);
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
        AssertNoteContains(report, "One or more completed sales have no persisted COGS; profit is incomplete.");
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
