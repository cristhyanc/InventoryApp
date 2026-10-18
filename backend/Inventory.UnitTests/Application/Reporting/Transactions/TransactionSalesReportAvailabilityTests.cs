using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Transactions;

/// <summary>
/// Issue #731 (under issue #730's model, parent #329 decision P9): the transaction sales report
/// reports how well recorded provider coverage accounts for the period's stored sales, so an empty
/// transaction list is never presented as a period in which nothing was sold.
///
/// <para>One state, not two: every figure here comes from the stored transactions, including the
/// per-transaction fee, which is an effective-dated estimate rather than an imported settlement
/// amount - so there is nothing for a settlement state to label.</para>
/// </summary>
public class TransactionSalesReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static Task<TransactionSalesReportDto> Report(TransactionSalesReportFacts facts) =>
        new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(facts))
            .Handle(new TransactionSalesFilterDto(From, To), CancellationToken.None);

    private static string Notes(TransactionSalesReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    [Fact]
    public async Task Complete_data_reports_the_sales_family_complete_and_adds_no_note()
    {
        var facts = FakeTransactionSalesReportFactsProvider.Facts(
            [FakeTransactionSalesReportFactsProvider.CompletedCardRow(transactionDate: new DateTime(2026, 7, 2))]);

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.SalesAvailability.State);
        Assert.True(report.SalesAvailability.IsComplete);
        Assert.DoesNotContain("accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fully_covered_period_with_no_transactions_stays_complete_so_its_empty_list_is_a_real_answer()
    {
        var report = await Report(FakeTransactionSalesReportFactsProvider.Empty());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.SalesAvailability.State);
        Assert.Empty(report.Rows);
        Assert.Equal(0m, report.Totals.Sales);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_an_empty_period()
    {
        var facts = FakeTransactionSalesReportFactsProvider.Facts([], coverage: TestReportCoverage.Missing());

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.SalesAvailability.State);
        Assert.False(report.SalesAvailability.IsComplete);
        Assert.Contains("no sales data is recorded", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_stay_listed_and_are_stale_as_of_the_last_covered_instant()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeTransactionSalesReportFactsProvider.Facts(
            [FakeTransactionSalesReportFactsProvider.CompletedCardRow(sale: 10m, transactionDate: new DateTime(2026, 7, 2))],
            coverage: TestReportCoverage.StaleSales(lastCovered));

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.SalesAvailability.State);
        Assert.Equal(lastCovered, report.SalesAvailability.AsOfUtc);
        // The stored transaction is still listed and still totalled: only the state changed.
        Assert.Single(report.Rows);
        Assert.Equal(10m, report.Totals.Sales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_report_whose_facts_carry_no_coverage_is_unverified_and_never_complete()
    {
        var facts = FakeTransactionSalesReportFactsProvider.Facts([]) with { Coverage = new ReportCoverageFacts() };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.SalesAvailability.State);
        Assert.False(report.SalesAvailability.IsComplete);
    }
}
