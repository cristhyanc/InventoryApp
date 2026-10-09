using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Gst;

public class GetGstAccountingAidTests
{
    private static BookkeepingReportDto CompleteBookkeeping(decimal sales = 110m, decimal gstOnSales = 10m,
        decimal nayaxFeesExGst = 5m, decimal gstOnFees = 1m, decimal operatingExpenseGst = 0m) => new(
        From: new DateTime(2025, 8, 1), To: new DateTime(2025, 8, 31), FinancialYear: "FY2025-26",
        Sales: sales, CostOfGoods: 40m, GrossProfit: sales - 40m,
        Fees: nayaxFeesExGst, NetSettlement: sales - nayaxFeesExGst, GstOnSales: gstOnSales, GstOnFees: gstOnFees,
        DataQuality: new ReportingDataQualityDto(), NayaxFeesExGst: nayaxFeesExGst)
        {
            OperatingExpenseGst = operatingExpenseGst
        };

    [Fact]
    public async Task Derives_taxable_sales_and_net_gst_from_the_bookkeeping_reports_own_gst_figures()
    {
        var bookkeeping = CompleteBookkeeping(sales: 110m, gstOnSales: 10m, nayaxFeesExGst: 5m, gstOnFees: 1m, operatingExpenseGst: 0m);
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(bookkeeping),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(new DateTime(2025, 8, 1), report.From);
        Assert.Equal(new DateTime(2025, 8, 31), report.To);
        Assert.Equal(100m, report.TaxableSales);
        Assert.Equal(10m, report.GstOnSales);
        Assert.Equal(5m, report.TaxableFees);
        Assert.Equal(1m, report.GstOnFees);
        Assert.Equal(9m, report.NetGst);
        Assert.Equal(0m, report.OperatingExpenseGst);
        Assert.True(report.DataQuality.MissingStatus);
        Assert.True(report.DataQuality.HistoricalCostUnavailable);
        Assert.False(report.DataQuality.GstClassificationMissing);
        Assert.True(report.DataQuality.CommissionNotPersisted);
        Assert.False(report.DataQuality.ContainsUnmappedProducts);
    }

    [Fact]
    public async Task Absent_imported_gst_classification_sets_the_gst_classification_missing_flag()
    {
        var bookkeeping = CompleteBookkeeping();
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(bookkeeping),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete(importedContainsGstClassification: false)));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.True(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task Operating_expense_gst_reduces_net_gst()
    {
        var bookkeeping = CompleteBookkeeping(operatingExpenseGst: 2m);
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(bookkeeping),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(2m, report.OperatingExpenseGst);
        Assert.Equal(7m, report.NetGst);
    }

    [Fact]
    public async Task Passes_the_bookkeeping_reports_resolved_range_and_the_filters_machine_id_to_the_facts_port()
    {
        var bookkeeping = CompleteBookkeeping();
        var factsProvider = new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete());
        var useCase = new GetGstAccountingAid(new FakeGetBookkeepingReport(bookkeeping), factsProvider);

        await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Equal((new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), (long?)10), factsProvider.LastRequest);
    }

    [Fact]
    public async Task Data_quality_carries_the_standard_reporting_disclaimers_with_no_extra_note()
    {
        var bookkeeping = CompleteBookkeeping();
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(bookkeeping),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(4, report.DataQuality.Notes!.Count);
        Assert.Contains("GST classification is not persisted on sales", string.Join(" ", report.DataQuality.Notes!));
    }

    private static PurchaseGstComponents Purchase(
        IReadOnlyList<PurchaseGstLine> lines,
        PurchaseGstCharge? delivery = null,
        PurchaseGstCharge? package = null) =>
        new(lines, delivery ?? new PurchaseGstCharge(null, GstClassification.Unknown),
            package ?? new PurchaseGstCharge(null, GstClassification.Unknown));

    private static async Task<GstAccountingAidDto> ReportForAsync(IReadOnlyList<PurchaseGstComponents> purchases)
    {
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(CompleteBookkeeping()),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete(purchases: purchases)));
        return await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);
    }

    [Fact]
    public async Task A_period_with_no_purchases_reports_no_input_gst_and_nothing_unresolved()
    {
        var report = await ReportForAsync([]);

        Assert.Equal(0m, report.PurchaseLineGst);
        Assert.Equal(0m, report.PurchaseChargeGst);
        Assert.Equal(0m, report.InventoryPurchaseGst);
        Assert.Equal(0, report.PurchaseUnresolvedComponentCount);
        Assert.Equal(0m, report.PurchaseUnresolvedAmount);
        Assert.False(report.PurchaseGstIncomplete);
        Assert.Equal(9m, report.NetGst);
    }

    [Fact]
    public async Task Purchase_input_gst_is_split_into_lines_and_charges_and_reduces_net_gst()
    {
        var report = await ReportForAsync(
        [
            Purchase(
                [new PurchaseGstLine(2m, 5.50m, GstClassification.Taxable)],
                delivery: new PurchaseGstCharge(11m, GstClassification.Taxable))
        ]);

        Assert.Equal(1.00m, report.PurchaseLineGst);
        Assert.Equal(1.00m, report.PurchaseChargeGst);
        Assert.Equal(2.00m, report.InventoryPurchaseGst);
        Assert.Equal(7m, report.NetGst);
        Assert.False(report.PurchaseGstIncomplete);
    }

    [Fact]
    public async Task Mixed_taxable_gst_free_and_unknown_purchases_keep_the_known_gst_and_flag_the_result_incomplete()
    {
        var report = await ReportForAsync(
        [
            Purchase([new PurchaseGstLine(1m, 110m, GstClassification.Taxable)]),
            Purchase([new PurchaseGstLine(1m, 50m, GstClassification.GstFree)]),
            Purchase([new PurchaseGstLine(1m, 22m, GstClassification.Unknown)])
        ]);

        Assert.Equal(10m, report.PurchaseLineGst);
        Assert.Equal(10m, report.InventoryPurchaseGst);
        Assert.Equal(1, report.PurchaseUnresolvedComponentCount);
        Assert.Equal(22m, report.PurchaseUnresolvedAmount);
        Assert.True(report.PurchaseGstIncomplete);
        Assert.Equal(-1m, report.NetGst);
        Assert.Contains("1 purchase component", string.Join(" ", report.DataQuality.Notes!));
    }

    [Fact]
    public async Task Unresolved_purchase_components_never_contribute_an_inferred_gst_amount()
    {
        var report = await ReportForAsync(
        [
            Purchase([new PurchaseGstLine(1m, 110m, GstClassification.Unknown)])
        ]);

        Assert.Equal(0m, report.InventoryPurchaseGst);
        Assert.Equal(110m, report.PurchaseUnresolvedAmount);
        Assert.Equal(9m, report.NetGst);
    }

    [Fact]
    public async Task A_null_or_zero_charge_does_not_inflate_the_unresolved_count()
    {
        var report = await ReportForAsync(
        [
            Purchase(
                [new PurchaseGstLine(1m, 10m, GstClassification.Taxable)],
                delivery: new PurchaseGstCharge(null, GstClassification.Unknown),
                package: new PurchaseGstCharge(0m, GstClassification.Unknown))
        ]);

        Assert.Equal(0, report.PurchaseUnresolvedComponentCount);
        Assert.Equal(0m, report.PurchaseUnresolvedAmount);
        Assert.False(report.PurchaseGstIncomplete);
    }

    [Fact]
    public async Task The_existing_imported_rows_gst_flag_is_not_what_marks_purchases_incomplete()
    {
        var report = await ReportForAsync([Purchase([new PurchaseGstLine(1m, 22m, GstClassification.Unknown)])]);

        Assert.True(report.PurchaseGstIncomplete);
        Assert.False(report.DataQuality.GstClassificationMissing);
    }

    [Fact]
    public async Task A_machine_filtered_report_excludes_whole_business_purchases_and_says_so()
    {
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(CompleteBookkeeping()),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Equal(0m, report.InventoryPurchaseGst);
        Assert.True(report.PurchaseGstIncomplete);
        Assert.Contains("not allocated to individual machines", string.Join(" ", report.DataQuality.Notes!));
    }

    [Fact]
    public async Task Zero_sales_produce_zero_taxable_sales_and_zero_net_gst()
    {
        var bookkeeping = CompleteBookkeeping(sales: 0m, gstOnSales: 0m, nayaxFeesExGst: 0m, gstOnFees: 0m);
        var useCase = new GetGstAccountingAid(
            new FakeGetBookkeepingReport(bookkeeping),
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(0m, report.TaxableSales);
        Assert.Equal(0m, report.NetGst);
    }
}
