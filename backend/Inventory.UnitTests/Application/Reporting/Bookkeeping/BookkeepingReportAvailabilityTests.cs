using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Bookkeeping;

/// <summary>
/// Issue #730 (parent #329 decision P9): the bookkeeping report reports how well recorded provider
/// coverage accounts for its provider-sourced figures, so missing provider data is never presented
/// as a real zero, and the business's own delivery/package and operating-expense figures stay
/// visible and exact whatever the provider data looks like.
/// </summary>
public class BookkeepingReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);
    private static readonly DateTime EndExclusive = new(2026, 8, 1);

    private static Task<BookkeepingReportDto> Report(BookkeepingReportFacts facts) =>
        new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(BookkeepingReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    private static BookkeepingReportFacts NoProviderData(BookkeepingReportFacts facts) => facts with
    {
        GrossSales = 0m,
        CardSales = 0m,
        CashSales = 0m,
        CardTransactions = 0,
        CashTransactions = 0,
        PartialCostOfGoods = 0m,
        ImportedHasNetSettlement = false,
        ImportedNetSettlement = 0m,
        ImportedContainsRows = false,
        ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null),
        SalesCoverage = [],
        SettlementCoverage = [],
        ContainsSalesRows = false,
    };

    [Fact]
    public async Task A_fully_covered_period_reports_every_provider_figure_complete()
    {
        var report = await Report(FakeBookkeepingReportFactsProvider.Complete());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.True(report.ProviderAvailability.Sales.IsComplete);
        Assert.DoesNotContain("coverage", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fully_covered_period_with_no_sales_stays_complete_so_its_zero_is_a_real_zero()
    {
        var facts = NoProviderData(FakeBookkeepingReportFactsProvider.Complete()) with
        {
            SalesCoverage = [new ReportCoverageRange(From.AddDays(-10), EndExclusive.AddDays(2))],
            SettlementCoverage = [new ReportCoverageRange(From.AddDays(-10), EndExclusive.AddDays(2))],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(0m, report.Sales);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_a_zero_period()
    {
        var report = await Report(NoProviderData(FakeBookkeepingReportFactsProvider.Complete()));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.Contains("no sales data", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sales_covered_only_up_to_a_point_are_stale_as_of_that_instant()
    {
        // Previously imported sales after a provider is disconnected: still reported, labelled.
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SalesCoverage = [new ReportCoverageRange(From.AddDays(-30), lastCovered)],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Sales.AsOfUtc);
        Assert.Equal(100m, report.Sales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sales_stored_before_coverage_recording_began_are_unverified_not_complete_and_not_zero()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with { SalesCoverage = [] };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
        Assert.Equal(100m, report.Sales);
        Assert.Contains("not accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_gap_left_by_a_failed_run_makes_the_period_unverified()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SalesCoverage =
            [
                new ReportCoverageRange(From.AddDays(-1), new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)),
                new ReportCoverageRange(new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc), EndExclusive),
            ],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
    }

    [Fact]
    public async Task Settlement_coverage_is_decided_separately_from_sales_coverage()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SettlementCoverage = [],
            ImportedContainsRows = false,
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
    }

    [Fact]
    public async Task Local_delivery_package_and_operating_expense_figures_stay_visible_with_no_provider_data()
    {
        var report = await Report(NoProviderData(FakeBookkeepingReportFactsProvider.Complete()));

        Assert.Equal(2m, report.DeliveryCosts);
        Assert.Equal(1m, report.PackageCosts);
        Assert.Equal(1m, report.StructuredOperatingExpenses);
        Assert.Equal(0.1m, report.OperatingExpenseGst);
    }
}
