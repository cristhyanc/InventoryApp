using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Dashboard;

/// <summary>
/// Issue #731 (under issue #730's model, parent #329 decision P9): <c>/api/reports/dashboard</c>
/// republishes the states the bookkeeping report decided - it composes that report for every
/// provider figure it shows - so the summary cards and the bookkeeping report can never disagree
/// about whether a zero was measured.
/// </summary>
public class DashboardReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static ReportAvailabilityDto State(ReportAvailabilityState state, DateTime? asOfUtc = null) =>
        new(state.ToString(), asOfUtc);

    private static BookkeepingReportDto Bookkeeping(
        ReportAvailabilityDto sales, ReportAvailabilityDto fees, decimal salesAmount = 110m) => new(
        From: From, To: To, FinancialYear: "FY2026-27",
        Sales: salesAmount, CostOfGoods: 50m, GrossProfit: 60m, Fees: 4m, NetSettlement: 75.6m,
        GstOnSales: 10m, GstOnFees: 0.4m, DataQuality: new ReportingDataQualityDto(),
        SiteCommission: 3m, NetProfit: 40m, NetMarginPercent: 36.36m, NayaxFeesExGst: 4m,
        NayaxFeesIncludingGst: 4.4m, DeliveryCosts: 2m, PackageCosts: 1m, OtherOperatingExpenses: 1m,
        CardSales: 80m, CashSales: 30m, CardTransactionCount: 8, CashTransactionCount: 2)
        {
            PartialCostOfGoods = 50m,
            IsCogsComplete = true,
            NayaxProcessingFees = new NayaxProcessingFeeResult(4m, 0.4m, 4.4m, 0m, 0m, 0m, 0, DateTime.UtcNow, null),
            ProviderAvailability = ReportProviderAvailabilityDto.From(sales, fees)
        };

    private static ProductProfitabilityReportDto EmptyProductReport() =>
        new(From, To, [], new ReportingDataQualityDto());

    private static Task<DashboardReportDto> Report(BookkeepingReportDto bookkeeping) =>
        new GetDashboardReport(
                new FakeGetBookkeepingReport(bookkeeping),
                new FakeGetProductProfitabilityReport(EmptyProductReport()),
                new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()))
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(DashboardReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    [Fact]
    public async Task Complete_data_reports_every_family_complete_and_adds_no_note()
    {
        var report = await Report(Bookkeeping(
            State(ReportAvailabilityState.Complete), State(ReportAvailabilityState.Complete)));

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Combined.State);
        Assert.DoesNotContain("accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_sales_data_is_missing_rather_than_a_zero_period()
    {
        var report = await Report(Bookkeeping(
            State(ReportAvailabilityState.Missing), State(ReportAvailabilityState.Missing), salesAmount: 0m));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.False(report.ProviderAvailability.Sales.IsComplete);
        Assert.Contains("no sales data is recorded", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no settlement data is imported", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_stay_reported_and_are_stale_as_of_the_last_covered_instant()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);

        var report = await Report(Bookkeeping(
            State(ReportAvailabilityState.Stale, lastCovered), State(ReportAvailabilityState.Complete)));

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Sales.AsOfUtc);
        Assert.Equal(110m, report.Sales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_states_are_exactly_the_bookkeeping_reports_never_decided_a_second_time()
    {
        var bookkeeping = Bookkeeping(
            State(ReportAvailabilityState.Unverified), State(ReportAvailabilityState.Missing));

        var report = await Report(bookkeeping);

        Assert.Equal(bookkeeping.ProviderAvailability, report.ProviderAvailability);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Combined.State);
    }
}
