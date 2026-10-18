using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Reconciliation;

/// <summary>
/// Issue #731 (under issue #730's model, parent #329 decision P9): the reconciliation report reports
/// how well recorded provider coverage accounts for each side it compares, so a difference of
/// <c>$0.00</c> between two sides that were never both measured is never presented as a reconciled
/// period.
/// </summary>
public class ReconciliationReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static Task<ReconciliationReportDto> Report(ReconciliationReportFacts facts) =>
        new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To), 0.01m, CancellationToken.None);

    private static string Notes(ReconciliationReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    private static ReconciliationReportFacts Facts(ReportCoverageFacts? coverage = null) =>
        FakeReconciliationReportFactsProvider.SinglePeriod(
            FakeReconciliationReportFactsProvider.Period(from: From, to: To), coverage: coverage);

    [Fact]
    public async Task Complete_data_reports_every_family_complete_and_adds_no_note()
    {
        var report = await Report(Facts());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Combined.State);
        Assert.DoesNotContain("accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_a_zero_period()
    {
        var report = await Report(Facts(TestReportCoverage.Missing()));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Combined.State);
        Assert.Contains("no sales data is recorded", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no settlement data is imported", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_stay_reported_and_are_stale_as_of_the_last_covered_instant()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);

        var report = await Report(Facts(TestReportCoverage.StaleSales(lastCovered)));

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Sales.AsOfUtc);
        // The recorded card sales are still reported in full; only the state changed.
        Assert.Equal(80m, report.CardSales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_difference_carries_the_least_complete_of_the_two_sides()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);

        var report = await Report(Facts(TestReportCoverage.StaleSales(lastCovered)));

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Combined.State);
    }

    [Fact]
    public async Task A_report_whose_facts_carry_no_coverage_is_unverified_and_never_complete()
    {
        var report = await Report(Facts(new ReportCoverageFacts()));

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
        Assert.False(report.ProviderAvailability.Combined.IsComplete);
    }
}
