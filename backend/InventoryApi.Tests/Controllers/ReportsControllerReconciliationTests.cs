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

public class ReportsControllerReconciliationTests
{
    [Fact]
    public async Task Reconciliation_action_routes_through_the_application_use_case()
    {
        var period = FakeReconciliationReportFactsProvider.Period(reportedGross: 250m, cardSales: 250m,
            processingFeesExGst: 5m, feeGst: 0.5m, actualNetReimbursement: 244.5m);
        var facts = FakeReconciliationReportFactsProvider.SinglePeriod(period);
        var reconciliationUseCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(facts));
        var bookkeepingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitabilityUseCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()));
        var gstUseCase = new GetGstAccountingAid(bookkeepingUseCase, new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboardUseCase = new GetDashboardReport(bookkeepingUseCase,
            new FakeGetProductProfitabilityReport(new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var getTransactionSalesReport = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        var getReportExportRows = new GetReportExportRows(bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase, dashboardUseCase, getTransactionSalesReport);
        var controller = new ReportsController(getReportExportRows, bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase, dashboardUseCase, getTransactionSalesReport);

        var report = await controller.Reconciliation(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Equal(250m, report.NayaxReportedGrossCardSales);
        Assert.True(report.IsMatch);
        Assert.False(report.DataQuality.GstClassificationMissing);
        Assert.False(report.PeriodRows[0].DataQuality.GstClassificationMissing);
    }
}
