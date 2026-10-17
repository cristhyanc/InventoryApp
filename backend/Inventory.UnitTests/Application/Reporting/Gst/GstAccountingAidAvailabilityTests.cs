using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting.Availability;
using Inventory.Domain.Reporting.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Gst;

/// <summary>
/// Issue #730 (parent #329 decision P9): the GST accounting aid carries the provider availability
/// its sales-GST and fee figures were decided with, keeps the business's own purchase and
/// operating-expense GST visible whatever the provider data looks like, and stops warning about
/// missing GST classification on settlement rows that were never imported.
/// </summary>
public class GstAccountingAidAvailabilityTests
{
    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private static ReportAvailabilityDto State(ReportAvailabilityState state, DateTime? asOf = null) =>
        new(state.ToString(), asOf);

    private static BookkeepingReportDto Bookkeeping(
        ReportAvailabilityDto sales, ReportAvailabilityDto fees, decimal gstOnSales = 10m, decimal gstOnFees = 1m) =>
        new(From, To, "FY2026-27",
            Sales: 110m, CostOfGoods: 40m, GrossProfit: 70m,
            Fees: 5m, NetSettlement: 105m, GstOnSales: gstOnSales, GstOnFees: gstOnFees,
            DataQuality: new ReportingDataQualityDto(), NayaxFeesExGst: 5m)
        {
            OperatingExpenseGst = 0.5m,
            ProviderAvailability = new ReportProviderAvailabilityDto(sales, fees),
        };

    private static Task<GstAccountingAidDto> Report(BookkeepingReportDto bookkeeping, GstReportFacts facts) =>
        new GetGstAccountingAid(new FakeGetBookkeepingReport(bookkeeping), new FakeGstReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(GstAccountingAidDto report) => string.Join(" ", report.DataQuality.Notes!);

    /// <summary>One taxable $110 purchase line: $10 of input GST, fully classified.</summary>
    private static PurchaseGstComponents TaxablePurchase() => new(
        [new PurchaseGstLine(1m, 110m, GstClassification.Taxable)],
        new PurchaseGstCharge(null, GstClassification.Unknown),
        new PurchaseGstCharge(null, GstClassification.Unknown));

    [Fact]
    public async Task The_provider_availability_is_carried_through_from_the_bookkeeping_report()
    {
        var asOf = new DateTime(2026, 8, 12, 4, 0, 0, DateTimeKind.Utc);

        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Stale, asOf), State(ReportAvailabilityState.Missing)),
            FakeGstReportFactsProvider.Complete());

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(asOf, report.ProviderAvailability.Sales.AsOfUtc);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
    }

    [Fact]
    public async Task Net_gst_is_only_as_complete_as_the_weaker_of_its_two_provider_inputs()
    {
        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Complete), State(ReportAvailabilityState.Unverified)),
            FakeGstReportFactsProvider.Complete());

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.NetGstAvailability.State);
        Assert.False(report.NetGstAvailability.IsComplete);
    }

    [Fact]
    public async Task Net_gst_is_complete_only_when_both_provider_inputs_are()
    {
        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Complete), State(ReportAvailabilityState.Complete)),
            FakeGstReportFactsProvider.Complete());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.NetGstAvailability.State);
        Assert.DoesNotContain("not accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Purchase_and_operating_expense_gst_stay_visible_when_no_provider_data_exists()
    {
        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Missing), State(ReportAvailabilityState.Missing),
                gstOnSales: 0m, gstOnFees: 0m),
            FakeGstReportFactsProvider.Complete(importedContainsRows: false, purchases: [TaxablePurchase()]));

        Assert.Equal(0.5m, report.OperatingExpenseGst);
        Assert.Equal(10m, report.InventoryPurchaseGst);
        Assert.Equal(10m, report.PurchaseLineGst);
        Assert.False(report.PurchaseGstIncomplete);
    }

    [Fact]
    public async Task No_settlement_import_at_all_raises_no_gst_classification_warning()
    {
        // The classification flag is about imported reimbursement rows. With no settlement file
        // imported, there are no rows to be missing a classification, and the availability state is
        // what says the fee figures are unavailable.
        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Complete), State(ReportAvailabilityState.Missing)),
            FakeGstReportFactsProvider.Complete(importedContainsRows: false, importedContainsGstClassification: false));

        Assert.False(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Imported_settlement_rows_without_a_classification_still_raise_the_warning()
    {
        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Complete), State(ReportAvailabilityState.Complete)),
            FakeGstReportFactsProvider.Complete(importedContainsRows: true, importedContainsGstClassification: false));

        Assert.True(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task An_incomplete_provider_figure_is_reported_as_a_data_quality_note()
    {
        var report = await Report(
            Bookkeeping(State(ReportAvailabilityState.Missing), State(ReportAvailabilityState.Missing)),
            FakeGstReportFactsProvider.Complete(importedContainsRows: false));

        Assert.Contains("GST on sales", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", Notes(report), StringComparison.OrdinalIgnoreCase);
    }
}
