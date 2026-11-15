using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting.Availability;
using Inventory.Domain.Reporting.Gst;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Gst;

/// <summary>
/// The GST and BAS summary Bookkeeping shows for a GST-registered business (issue #1318). The GST
/// accounting aid's real use case is composed here rather than stubbed, because "the two reports
/// agree for the same period" is the property under test.
/// </summary>
public class GetGstBasSummaryTests
{
    private static readonly ReportAvailabilityDto Complete =
        new(nameof(ReportAvailabilityState.Complete), null);

    private static readonly ReportingFilterDto WholeBusinessAugust =
        new(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31));

    private static BookkeepingReportDto Bookkeeping(
        decimal sales = 330m,
        decimal gstOnSales = 30m,
        decimal gstOnFees = 3m,
        decimal operatingExpenseGst = 4m,
        decimal? taxableSales = null,
        decimal gstFreeSales = 0m,
        decimal noGstRuleSales = 0m,
        ReportAvailabilityDto? salesAvailability = null,
        ReportAvailabilityDto? feesAvailability = null,
        NayaxProcessingFeeResult? fees = null) => new(
        From: new DateTime(2025, 8, 1), To: new DateTime(2025, 8, 31), FinancialYear: "FY2025-26",
        Sales: sales, CostOfGoods: 0m, GrossProfit: sales,
        Fees: 0m, NetSettlement: sales, GstOnSales: gstOnSales, GstOnFees: gstOnFees,
        DataQuality: new ReportingDataQualityDto())
        {
            TaxableSales = taxableSales ?? sales - gstFreeSales,
            GstFreeSales = gstFreeSales,
            NoGstRuleSales = noGstRuleSales,
            OperatingExpenseGst = operatingExpenseGst,
            NayaxProcessingFees = fees ?? new NayaxProcessingFeeResult(0m, gstOnFees, 0m, 0m, 0m, 0m, 0, null, null),
            ProviderAvailability = ReportProviderAvailabilityDto.From(
            salesAvailability ?? Complete, feesAvailability ?? Complete)
        };

    private static GetGstAccountingAid Aid(
        BookkeepingReportDto bookkeeping,
        IReadOnlyList<PurchaseGstComponents>? purchases = null) =>
        new(new FakeGetBookkeepingReport(bookkeeping),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete(purchases: purchases)));

    /// <summary>A taxable product line whose GST-inclusive amount is <paramref name="amount"/>.</summary>
    private static PurchaseGstComponents TaxablePurchase(decimal amount) =>
        new([new PurchaseGstLine(1m, amount, GstClassification.Taxable)],
            new PurchaseGstCharge(null, GstClassification.Unknown),
            new PurchaseGstCharge(null, GstClassification.Unknown));

    private static PurchaseGstComponents UnclassifiedPurchase(decimal amount) =>
        new([new PurchaseGstLine(1m, amount, GstClassification.Unknown)],
            new PurchaseGstCharge(null, GstClassification.Unknown),
            new PurchaseGstCharge(null, GstClassification.Unknown));

    private static GetGstBasSummary Summary(
        GetGstAccountingAid aid,
        bool? registered = true,
        FakeGstRegistrationStore? registration = null,
        FakeCurrentBusinessProvider? currentBusiness = null) =>
        new(aid, currentBusiness ?? new FakeCurrentBusinessProvider(7),
            registration ?? new FakeGstRegistrationStore(registered));

    [Fact]
    public async Task A_gst_registered_business_gets_the_bas_labels_for_the_whole_business_range()
    {
        // The issue's own worked example: 1A $30 with $8 of purchase GST, $4 of expense GST and $3
        // of fee GST gives 1B $15 and $15 payable.
        var summary = Summary(Aid(Bookkeeping(), [TaxablePurchase(88m)]));

        var result = await summary.Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Equal(GstBasSummaryStatus.Available, result.Status);
        Assert.Equal(new DateTime(2025, 8, 1), result.From);
        Assert.Equal(new DateTime(2025, 8, 31), result.To);
        var figures = Assert.IsType<GstBasSummaryFiguresDto>(result.Figures);
        Assert.Equal(330m, figures.TotalSales);
        Assert.Equal(30m, figures.GstOnSales);
        Assert.Equal(15m, figures.GstCredits);
        Assert.Equal(8m, figures.PurchaseGstCredits);
        Assert.Equal(4m, figures.OperatingExpenseGstCredits);
        Assert.Equal(3m, figures.NayaxFeeGstCredits);
        Assert.Equal(15m, figures.NetGst);
        Assert.Equal(nameof(BasNetGstOutcome.Payable), figures.NetGstOutcome);
        Assert.False(figures.IsIncomplete);
    }

    [Fact]
    public async Task G1_is_the_whole_of_sales_including_gst_free_sales()
    {
        // $300 of taxable product sales and $30 of GST-free sales. G1 is all $330; 1A is the
        // eleventh of the taxable $300 only.
        var summary = Summary(Aid(Bookkeeping(sales: 330m, gstOnSales: 27.27m, gstFreeSales: 30m)));

        var figures = (await summary.Handle(WholeBusinessAugust, CancellationToken.None)).Figures!;

        Assert.Equal(330m, figures.TotalSales);
        Assert.Equal(30m, figures.GstFreeSales);
        Assert.Equal(27.27m, figures.GstOnSales);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task A_business_that_is_not_registered_for_gst_gets_no_summary_and_no_figures(bool? registered)
    {
        var bookkeeping = new FakeGetBookkeepingReport(Bookkeeping());
        var aid = new GetGstAccountingAid(bookkeeping,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var result = await Summary(aid, registered).Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Equal(GstBasSummaryStatus.NotGstRegistered, result.Status);
        Assert.Null(result.Figures);
        Assert.Empty(result.DataQuality.Notes!);
        // The range is still the requested one, so a client can label what it is not showing.
        Assert.Equal(new DateTime(2025, 8, 1), result.From);
        Assert.Equal(new DateTime(2025, 8, 31), result.To);
        // Nothing was calculated at all: an unregistered business's GST is not a report anybody ran.
        Assert.Null(bookkeeping.LastRequest);
    }

    [Fact]
    public async Task A_machine_filtered_request_reports_that_scope_instead_of_a_per_machine_estimate()
    {
        var bookkeeping = new FakeGetBookkeepingReport(Bookkeeping());
        var aid = new GetGstAccountingAid(bookkeeping,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var result = await Summary(aid).Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10),
            CancellationToken.None);

        Assert.Equal(GstBasSummaryStatus.MachineFiltered, result.Status);
        Assert.Null(result.Figures);
        Assert.Null(bookkeeping.LastRequest);
    }

    [Fact]
    public async Task Registration_is_read_for_the_business_the_callers_membership_resolved()
    {
        var registration = new FakeGstRegistrationStore(true);

        await Summary(Aid(Bookkeeping()), registration: registration,
                currentBusiness: new FakeCurrentBusinessProvider(42))
            .Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Equal(BusinessId.From(42), registration.LastBusinessId);
    }

    [Fact]
    public async Task The_summary_and_the_gst_accounting_aid_report_the_same_period_identically()
    {
        var bookkeeping = Bookkeeping();
        var purchases = new[] { TaxablePurchase(88m) };

        var aidReport = await Aid(bookkeeping, purchases).Handle(WholeBusinessAugust, CancellationToken.None);
        var figures = (await Summary(Aid(bookkeeping, purchases)).Handle(WholeBusinessAugust, CancellationToken.None))
            .Figures!;

        Assert.Equal(aidReport.NetGst, figures.NetGst);
        Assert.Equal(aidReport.GstOnSales, figures.GstOnSales);
        Assert.Equal(aidReport.GstOnFees, figures.NayaxFeeGstCredits);
        Assert.Equal(aidReport.OperatingExpenseGst, figures.OperatingExpenseGstCredits);
        Assert.Equal(aidReport.InventoryPurchaseGst, figures.PurchaseGstCredits);
        Assert.Equal(aidReport.GstFreeSales, figures.GstFreeSales);
        Assert.Equal(aidReport.NoGstRuleSales, figures.NoGstRuleSales);
        Assert.Equal(aidReport.NetGstAvailability, figures.NetGstAvailability);
        Assert.Equal(aidReport.TaxableSales + aidReport.GstOnSales + aidReport.GstFreeSales, figures.TotalSales);
    }

    [Fact]
    public async Task Credits_above_gst_on_sales_are_reported_as_an_estimated_credit()
    {
        var summary = Summary(Aid(Bookkeeping(gstOnSales: 5m), [TaxablePurchase(110m)]));

        var figures = (await summary.Handle(WholeBusinessAugust, CancellationToken.None)).Figures!;

        Assert.Equal(17m, figures.GstCredits);
        Assert.Equal(-12m, figures.NetGst);
        Assert.Equal(nameof(BasNetGstOutcome.Credit), figures.NetGstOutcome);
    }

    [Fact]
    public async Task A_period_with_nothing_recorded_is_nil_and_is_not_called_incomplete()
    {
        var summary = Summary(Aid(Bookkeeping(sales: 0m, gstOnSales: 0m, gstOnFees: 0m, operatingExpenseGst: 0m)));

        var figures = (await summary.Handle(WholeBusinessAugust, CancellationToken.None)).Figures!;

        Assert.Equal(0m, figures.TotalSales);
        Assert.Equal(0m, figures.NetGst);
        Assert.Equal(nameof(BasNetGstOutcome.Nil), figures.NetGstOutcome);
        Assert.False(figures.IsIncomplete);
    }

    [Fact]
    public async Task Unclassified_purchase_components_are_reported_and_never_credited()
    {
        var summary = Summary(Aid(Bookkeeping(), [TaxablePurchase(88m), UnclassifiedPurchase(220m)]));

        var result = await summary.Handle(WholeBusinessAugust, CancellationToken.None);
        var figures = result.Figures!;

        Assert.Equal(8m, figures.PurchaseGstCredits);
        Assert.Equal(1, figures.PurchaseUnresolvedComponentCount);
        Assert.Equal(220m, figures.PurchaseUnresolvedAmount);
        Assert.True(figures.IsIncomplete);
        Assert.Contains("1 purchase component", string.Join(" ", result.DataQuality.Notes!));
    }

    [Fact]
    public async Task Sales_taxable_only_by_the_no_rule_default_are_disclosed_and_make_the_estimate_incomplete()
    {
        var summary = Summary(Aid(Bookkeeping(noGstRuleSales: 220m)));

        var result = await summary.Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Equal(220m, result.Figures!.NoGstRuleSales);
        Assert.True(result.Figures!.IsIncomplete);
        Assert.Contains("assumed taxable", string.Join(" ", result.DataQuality.Notes!));
    }

    [Fact]
    public async Task Estimated_fee_gst_is_disclosed_and_makes_the_estimate_incomplete()
    {
        var fees = new NayaxProcessingFeeResult(
            ActualFeeExGst: 0m, ActualFeeGst: 1m, ActualFeeIncGst: 0m,
            EstimatedFeeExGst: 0m, EstimatedFeeGst: 2m, EstimatedFeeIncGst: 0m,
            EstimatedCardTransactionCount: 9, ActualFeeCoverageEndDate: null, EstimatedFeeFromDate: null);
        var summary = Summary(Aid(Bookkeeping(gstOnFees: 3m, fees: fees)));

        var result = await summary.Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Equal(3m, result.Figures!.NayaxFeeGstCredits);
        Assert.Equal(2m, result.Figures!.EstimatedFeeGst);
        Assert.True(result.Figures!.FeeGstIncludesEstimates);
        Assert.True(result.Figures!.IsIncomplete);
        Assert.Contains("estimated from the configured processing-fee rate",
            string.Join(" ", result.DataQuality.Notes!));
    }

    [Fact]
    public async Task Incomplete_provider_coverage_travels_with_the_figures_and_makes_the_estimate_incomplete()
    {
        var missing = new ReportAvailabilityDto(nameof(ReportAvailabilityState.Missing), null);
        var summary = Summary(Aid(Bookkeeping(salesAvailability: missing)));

        var result = await summary.Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Equal(nameof(ReportAvailabilityState.Missing), result.Figures!.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), result.Figures!.NetGstAvailability.State);
        Assert.True(result.Figures!.IsIncomplete);
        // The shared calculation's own availability note, not a second wording written here.
        Assert.Contains("no provider data is recorded", string.Join(" ", result.DataQuality.Notes!));
    }

    [Fact]
    public async Task The_summary_reports_only_this_periods_own_notes_and_no_standing_disclaimer()
    {
        var result = await Summary(Aid(Bookkeeping())).Handle(WholeBusinessAugust, CancellationToken.None);

        Assert.Empty(result.DataQuality.Notes!);
        Assert.False(result.DataQuality.MissingStatus);
        Assert.False(result.DataQuality.CommissionNotPersisted);
        Assert.False(result.DataQuality.HistoricalCostUnavailable);
    }
}
