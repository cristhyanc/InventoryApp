using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.ProductProfitability;

/// <summary>
/// Issue #731 (under issue #730's model, parent #329 decision P9): the product profitability report
/// reports how well recorded provider coverage accounts for the period's stored sales, so an empty
/// product table is never presented as a period in which nothing sold.
///
/// <para>One state, not two: nothing in this report comes from a settlement import, and the
/// Last/Lowest purchase costs beside each product are this business's own records.</para>
/// </summary>
public class ProductProfitabilityReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static Task<ProductProfitabilityReportDto> Report(ProductProfitabilityReportFacts facts) =>
        new GetProductProfitabilityReport(
                new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty())
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(ProductProfitabilityReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    [Fact]
    public async Task Complete_data_reports_the_sales_family_complete_and_adds_no_note()
    {
        var report = await Report(FakeProductProfitabilityReportFactsProvider.SingleMappedProduct());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.SalesAvailability.State);
        Assert.True(report.SalesAvailability.IsComplete);
        Assert.DoesNotContain("accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fully_covered_period_with_no_sales_stays_complete_so_its_empty_table_is_a_real_answer()
    {
        var report = await Report(FakeProductProfitabilityReportFactsProvider.Empty());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.SalesAvailability.State);
        Assert.Empty(report.Rows);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_an_empty_table()
    {
        var report = await Report(
            FakeProductProfitabilityReportFactsProvider.Empty(TestReportCoverage.Missing()));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.SalesAvailability.State);
        Assert.False(report.SalesAvailability.IsComplete);
        Assert.Contains("no sales data is recorded", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_stay_reported_and_are_stale_as_of_the_last_covered_instant()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(
            sales: 30m, coverage: TestReportCoverage.StaleSales(lastCovered));

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.SalesAvailability.State);
        Assert.Equal(lastCovered, report.SalesAvailability.AsOfUtc);
        Assert.Equal(30m, Assert.Single(report.Rows).Sales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_report_whose_facts_carry_no_coverage_is_unverified_and_never_complete()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.Empty() with { Coverage = new ReportCoverageFacts() };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.SalesAvailability.State);
        Assert.False(report.SalesAvailability.IsComplete);
    }
}
