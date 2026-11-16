using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Infrastructure.Reporting;
using InventoryApi.Controllers;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Dashboard;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.MachineProfitability;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
using InventoryApi.Tests.Application.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ReportsControllerGstTests
{
    [Fact]
    public async Task Gst_action_routes_through_the_application_use_case()
    {
        var bookkeepingFacts = FakeBookkeepingReportFactsProvider.Complete(sales: 110m);
        var bookkeepingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(bookkeepingFacts));
        var gstUseCase = new GetGstAccountingAid(bookkeepingUseCase, new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var reconciliationUseCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitabilityUseCase = new GetProductProfitabilityReport(
            new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()),
            FakeProductPurchaseCostFactsProvider.Empty());
        var dashboardUseCase = new GetDashboardReport(bookkeepingUseCase,
            new FakeGetProductProfitabilityReport(new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var getTransactionSalesReport = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        var getReportExportRows = new GetReportExportRows(bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase, FakeGstRegistrationStore.BasSummary(gstUseCase), dashboardUseCase, getTransactionSalesReport);
        var controller = new ReportsController(getReportExportRows, new ReportExportFileWriter(),
            bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase, FakeGstRegistrationStore.BasSummary(gstUseCase), dashboardUseCase, getTransactionSalesReport);

        var report = await controller.Gst(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(100m, report.TaxableSales);
        Assert.False(report.DataQuality.GstClassificationMissing);
    }

    /// <summary>
    /// Issue #1318: the BAS summary Bookkeeping shows is a second action on the same controller, so
    /// it inherits the same <c>Reports.View</c> capability, and it reports the same period as the
    /// GST accounting aid above because it is the same calculation.
    /// </summary>
    [Fact]
    public async Task Gst_bas_summary_action_routes_through_the_application_use_case()
    {
        var bookkeepingUseCase = new GetBookkeepingReport(
            new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete(sales: 110m)));
        var gstUseCase = new GetGstAccountingAid(bookkeepingUseCase,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var controller = Controller(bookkeepingUseCase, gstUseCase);
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var summary = await controller.GstBasSummary(filter, CancellationToken.None);
        var aid = await controller.Gst(filter, CancellationToken.None);

        Assert.Equal(GstBasSummaryStatus.Available, summary.Status);
        Assert.Equal(110m, summary.Figures!.TotalSales);
        Assert.Equal(aid.GstOnSales, summary.Figures!.GstOnSales);
        Assert.Equal(aid.NetGst, summary.Figures!.NetGst);
    }

    private static ReportsController Controller(GetBookkeepingReport bookkeepingUseCase, GetGstAccountingAid gstUseCase)
    {
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var reconciliationUseCase = new GetReconciliationReport(
            new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(
            new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitabilityUseCase = new GetProductProfitabilityReport(
            new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()),
            FakeProductPurchaseCostFactsProvider.Empty());
        var dashboardUseCase = new GetDashboardReport(bookkeepingUseCase,
            new FakeGetProductProfitabilityReport(
                new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var getTransactionSalesReport = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        var getReportExportRows = new GetReportExportRows(bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase,
            FakeGstRegistrationStore.BasSummary(gstUseCase), dashboardUseCase, getTransactionSalesReport);
        return new ReportsController(getReportExportRows, new ReportExportFileWriter(),
            bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase,
            FakeGstRegistrationStore.BasSummary(gstUseCase), dashboardUseCase, getTransactionSalesReport);
    }
}
