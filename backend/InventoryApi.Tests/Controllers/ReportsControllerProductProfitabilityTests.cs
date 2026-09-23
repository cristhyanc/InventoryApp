using System;
using System.Threading;
using System.Threading.Tasks;
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

public class ReportsControllerProductProfitabilityTests
{
    [Fact]
    public async Task Product_profitability_action_routes_through_the_application_use_case()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 7, productName: "Soda", sales: 60m, cost: 20m);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));
        var bookkeepingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var reconciliationUseCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var gstUseCase = new GetGstAccountingAid(bookkeepingUseCase, new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboardUseCase = new GetDashboardReport(bookkeepingUseCase,
            new FakeGetProductProfitabilityReport(new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var getTransactionSalesReport = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        var getReportExportRows = new GetReportExportRows(bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, useCase, gstUseCase, dashboardUseCase, getTransactionSalesReport);
        var controller = new ReportsController(getReportExportRows, bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, useCase, gstUseCase, dashboardUseCase, getTransactionSalesReport);

        var report = await controller.ProductProfitability(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(7, row.ProductId);
        Assert.Equal("Soda", row.ProductName);
        Assert.Equal(60m, row.Sales);
        Assert.Equal(40m, row.GrossProfit);
        Assert.False(report.DataQuality.HistoricalCostUnavailable);
        Assert.False(report.DataQuality.ContainsUnmappedProducts);
    }
}
