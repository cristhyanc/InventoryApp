using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Dashboard;

public class GetDashboardReportTests
{
    private static BookkeepingReportDto CompleteBookkeeping(
        decimal sales = 110m, decimal? grossProfit = 60m, decimal cardSales = 80m, decimal cashSales = 30m,
        int cardTransactionCount = 8, int cashTransactionCount = 2, decimal nayaxFeesIncludingGst = 4.4m,
        decimal nayaxFeesExGst = 4m, decimal siteCommission = 3m, decimal? netProfit = 40m,
        decimal? netMarginPercent = 36.36m, decimal? directProfit = null, decimal? directMarginPercent = null,
        bool isCogsComplete = true, decimal partialCostOfGoods = 50m, bool hasMissingFeeRates = false) => new(
        From: new DateTime(2025, 8, 1), To: new DateTime(2025, 8, 31), FinancialYear: "FY2025-26",
        Sales: sales, CostOfGoods: isCogsComplete ? partialCostOfGoods : null, GrossProfit: grossProfit,
        Fees: nayaxFeesExGst, NetSettlement: cardSales - nayaxFeesIncludingGst, GstOnSales: 10m, GstOnFees: 0.4m,
        DataQuality: new ReportingDataQualityDto(), SiteCommission: siteCommission, NetProfit: netProfit,
        NetMarginPercent: netMarginPercent, NayaxFeesExGst: nayaxFeesExGst, NayaxFeesIncludingGst: nayaxFeesIncludingGst,
        DeliveryCosts: 2m, PackageCosts: 1m, OtherOperatingExpenses: 1m, CardSales: cardSales, CashSales: cashSales,
        CardTransactionCount: cardTransactionCount, CashTransactionCount: cashTransactionCount)
    {
        PartialCostOfGoods = partialCostOfGoods,
        IsCogsComplete = isCogsComplete,
        DirectProfit = directProfit,
        DirectMarginPercent = directMarginPercent,
        NayaxProcessingFees = hasMissingFeeRates
            ? new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null, MissingRateTransactionCount: 3)
            : new NayaxProcessingFeeResult(nayaxFeesExGst, 0.4m, nayaxFeesIncludingGst, 0m, 0m, 0m, 0, DateTime.UtcNow, null)
    };

    private static ProductProfitabilityReportDto EmptyProductReport(bool containsUnmapped = false, string note = null) => new(
        new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), [],
        new ReportingDataQualityDto(ContainsUnmappedProducts: containsUnmapped,
            Notes: note is null ? null : new List<string> { note }));

    private static GetDashboardReport UseCase(BookkeepingReportDto bookkeeping, ProductProfitabilityReportDto productReport,
        DashboardReportFacts facts) => new(
        new FakeGetBookkeepingReport(bookkeeping), new FakeGetProductProfitabilityReport(productReport),
        new FakeDashboardReportFactsProvider(facts));

    [Fact]
    public async Task Reuses_the_bookkeeping_reports_sales_profit_and_fee_figures_directly()
    {
        var bookkeeping = CompleteBookkeeping(sales: 200m, grossProfit: 120m, netProfit: 80m);
        var useCase = UseCase(bookkeeping, EmptyProductReport(), FakeDashboardReportFactsProvider.Complete());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(200m, report.Sales);
        Assert.Equal(200m, report.TotalSales);
        Assert.Equal(120m, report.GrossProfit);
        Assert.Equal(80m, report.NetProfit);
        Assert.Equal(bookkeeping.SiteCommission, report.SiteCommission);
        Assert.Equal(bookkeeping.NayaxFeesIncludingGst, report.NayaxFees);
        Assert.Equal(bookkeeping.CardSales, report.CardSales);
        Assert.Equal(bookkeeping.CashSales, report.CashSales);
    }

    [Fact]
    public async Task Transaction_machine_and_product_counts_come_from_the_dashboard_facts_port()
    {
        var facts = FakeDashboardReportFactsProvider.Complete(transactionCount: 25, machineCount: 4, productCount: 6);
        var useCase = UseCase(CompleteBookkeeping(sales: 250m), EmptyProductReport(), facts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(25, report.Transactions);
        Assert.Equal(25m, report.Quantity);
        Assert.Equal(4, report.MachineCount);
        Assert.Equal(6, report.ProductCount);
        Assert.Equal(10m, report.AverageSale);
    }

    [Fact]
    public async Task Unmapped_product_count_comes_from_the_product_profitability_report()
    {
        var productReport = new ProductProfitabilityReportDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31),
        [
            new ProductProfitabilityRowDto(1, "Water", null, 10m, 2m, 5m, 5m, 50m, 2, false, true),
            new ProductProfitabilityRowDto(null, "Unmapped product", null, 5m, 1m, null, null, null, 1, true, false)
        ], new ReportingDataQualityDto());
        var useCase = UseCase(CompleteBookkeeping(), productReport, FakeDashboardReportFactsProvider.Complete());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(1, report.UnmappedProductCount);
    }

    [Fact]
    public async Task Expected_reimbursement_is_card_sales_less_fees_and_actual_comes_from_the_facts_port()
    {
        var bookkeeping = CompleteBookkeeping(cardSales: 100m, nayaxFeesIncludingGst: 5m);
        var facts = FakeDashboardReportFactsProvider.Complete(importedNetSettlement: 95m, importedContainsRows: true);
        var useCase = UseCase(bookkeeping, EmptyProductReport(), facts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal(95m, report.ExpectedReimbursement);
        Assert.Equal(95m, report.ActualReimbursement);
        Assert.Equal(0m, report.ReimbursementDifference);
        Assert.True(report.IsReconciled);
        Assert.Equal("Reconciled", report.ReconciliationStatus);
    }

    [Fact]
    public async Task No_imported_rows_reports_pending_reconciliation()
    {
        var bookkeeping = CompleteBookkeeping(cardSales: 100m, nayaxFeesIncludingGst: 5m);
        var facts = FakeDashboardReportFactsProvider.Complete(importedContainsRows: false, importedNetSettlement: 0m);
        var useCase = UseCase(bookkeeping, EmptyProductReport(), facts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Equal("Pending", report.ReconciliationStatus);
        Assert.False(report.IsReconciled);
    }

    [Fact]
    public async Task Incomplete_cogs_adds_a_dashboard_specific_quality_note()
    {
        var bookkeeping = CompleteBookkeeping(isCogsComplete: false, grossProfit: null, netProfit: null);
        var useCase = UseCase(bookkeeping, EmptyProductReport(), FakeDashboardReportFactsProvider.Complete());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.False(report.IsCogsComplete);
        Assert.Null(report.CostOfGoodsSold);
        Assert.Null(report.GrossProfit);
        Assert.Null(report.GrossMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("profitability is unavailable"));
    }

    [Fact]
    public async Task Missing_fee_rates_add_a_quality_note_with_the_affected_transaction_count()
    {
        var bookkeeping = CompleteBookkeeping(hasMissingFeeRates: true);
        var useCase = UseCase(bookkeeping, EmptyProductReport(), FakeDashboardReportFactsProvider.Complete());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("3 card transaction(s) have no effective Nayax processing fee rate"));
    }

    [Fact]
    public async Task Machine_filtered_scope_adds_the_shared_overhead_note_and_omits_net_profit()
    {
        var bookkeeping = CompleteBookkeeping(netProfit: null, directProfit: 55m, directMarginPercent: 45m);
        var useCase = UseCase(bookkeeping, EmptyProductReport(), FakeDashboardReportFactsProvider.Complete());

        var report = await useCase.Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Null(report.NetProfit);
        Assert.Equal(55m, report.DirectProfit);
        Assert.Equal(45m, report.DirectMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("shared business overhead"));
    }

    [Fact]
    public async Task Incomplete_commission_configuration_adds_a_note_and_its_warnings()
    {
        var facts = new DashboardReportFacts(10, 2, 3, true, 74m, CommissionIsComplete: false,
            CommissionWarnings: ["Overlapping commission agreements cover one or more sales for the selected machine."]);
        var useCase = UseCase(CompleteBookkeeping(), EmptyProductReport(), facts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete; net profit is unavailable"));
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("Overlapping commission agreements"));
    }

    [Fact]
    public async Task Product_report_notes_and_unmapped_flag_are_carried_into_the_dashboard_quality()
    {
        var productReport = EmptyProductReport(containsUnmapped: true, note: "One or more sales could not be mapped to a Product.");
        var useCase = UseCase(CompleteBookkeeping(), productReport, FakeDashboardReportFactsProvider.Complete());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), CancellationToken.None);

        Assert.True(report.DataQuality.ContainsUnmappedProducts);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("could not be mapped to a Product"));
    }

    [Fact]
    public async Task Passes_the_bookkeeping_reports_resolved_range_and_the_filters_machine_id_to_the_facts_port()
    {
        var factsProvider = new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete());
        var useCase = new GetDashboardReport(new FakeGetBookkeepingReport(CompleteBookkeeping()),
            new FakeGetProductProfitabilityReport(EmptyProductReport()), factsProvider);

        await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Equal((new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), (long?)10), factsProvider.LastRequest);
    }
}
