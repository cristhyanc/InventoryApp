using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.MachineProfitability;

/// <summary>
/// Issue #731 (under issue #730's model, parent #329 decision P9): the machine profitability report
/// reports how well recorded provider coverage accounts for its provider-sourced figures, so an
/// empty machine table is never presented as a fleet that sold nothing.
/// </summary>
public class MachineProfitabilityReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static Task<MachineProfitabilityReportDto> Report(MachineProfitabilityReportFacts facts) =>
        new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(MachineProfitabilityReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    [Fact]
    public async Task Complete_data_reports_every_family_complete_and_adds_no_note()
    {
        var report = await Report(FakeMachineProfitabilityReportFactsProvider.SingleMachine());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Combined.State);
        Assert.DoesNotContain("accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fully_covered_period_with_no_machines_stays_complete_so_its_empty_table_is_a_real_answer()
    {
        var report = await Report(FakeMachineProfitabilityReportFactsProvider.Empty());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Empty(report.Rows);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_an_empty_table()
    {
        var report = await Report(
            FakeMachineProfitabilityReportFactsProvider.Empty(TestReportCoverage.Missing()));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.Contains("no sales data is recorded", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no settlement data is imported", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_stay_reported_and_are_stale_as_of_the_last_covered_instant()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(
            sales: 100m, coverage: TestReportCoverage.StaleSales(lastCovered));

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Sales.AsOfUtc);
        Assert.Equal(100m, Assert.Single(report.Rows).Sales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_profit_carries_the_least_complete_of_the_two_families()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(
            coverage: TestReportCoverage.StaleSales(lastCovered));

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Combined.State);
    }

    [Fact]
    public async Task A_report_whose_facts_carry_no_coverage_is_unverified_and_never_complete()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.Empty() with { Coverage = new ReportCoverageFacts() };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
        Assert.False(report.ProviderAvailability.Combined.IsComplete);
    }
}
