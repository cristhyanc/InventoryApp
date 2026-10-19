using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Daily;

/// <summary>
/// Issue #731 (under issue #730's model, parent #329 decision P9): the daily report reports how well
/// recorded provider coverage accounts for its provider-sourced figures, so a day with no sales data
/// is never presented as a day that sold nothing.
/// </summary>
public class DailyReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static Task<DailyReportDto> Report(DailyReportFacts facts) =>
        new GetDailyReport(new FakeDailyReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(DailyReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    [Fact]
    public async Task Complete_data_reports_every_family_complete_and_adds_no_note()
    {
        var report = await Report(FakeDailyReportFactsProvider.SingleDay());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Combined.State);
        Assert.DoesNotContain("accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fully_covered_period_with_no_sales_stays_complete_so_its_zero_is_a_real_zero()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(
            grossSales: 0m, transactionCount: 0, partialCostOfGoods: 0m, cardSales: 0m, cashSales: 0m,
            completedTransactionCount: 0, importedReimbursement: 0m, netReimbursement: 0m);

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(0m, report.Totals!.GrossSales);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_a_zero_period()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(coverage: TestReportCoverage.Missing());

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.False(report.ProviderAvailability.Sales.IsComplete);
        Assert.Contains("no sales data is recorded", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no settlement data is imported", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_stay_reported_and_are_stale_as_of_the_last_covered_instant()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeDailyReportFactsProvider.SingleDay(coverage: TestReportCoverage.StaleSales(lastCovered));

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Sales.AsOfUtc);
        // The figures themselves are untouched: previously imported sales stay visible.
        Assert.Equal(100m, report.Totals!.GrossSales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_combined_state_is_the_least_complete_of_the_two_families()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeDailyReportFactsProvider.SingleDay(coverage: TestReportCoverage.StaleSales(lastCovered));

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Combined.State);
    }

    [Fact]
    public async Task A_report_whose_facts_carry_no_coverage_is_unverified_and_never_complete()
    {
        // The conservative default: a state nothing decided must not license a zero.
        var facts = FakeDailyReportFactsProvider.SingleDay() with { Coverage = new ReportCoverageFacts() };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
        Assert.False(report.ProviderAvailability.Sales.IsComplete);
    }
}
