using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Dashboard;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.MachineProfitability;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
using InventoryApi.Tests.Application.Reporting.Transactions;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ReportsControllerBookkeepingTests
{
    [Fact]
    public async Task Bookkeeping_action_routes_through_the_application_use_case_not_the_legacy_service()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete(sales: 250m, cost: 90m);
        var useCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var reconciliationUseCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitabilityUseCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()));
        var gstUseCase = new GetGstAccountingAid(useCase, new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboardUseCase = new GetDashboardReport(useCase,
            new FakeGetProductProfitabilityReport(new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var legacyService = new Mock<IReportingService>(MockBehavior.Strict);
        var getTransactionSalesReport = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        var controller = new ReportsController(legacyService.Object, useCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase, dashboardUseCase, getTransactionSalesReport);

        var report = await controller.Bookkeeping(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(250m, report.Sales);
        Assert.Equal(90m, report.CostOfGoods);
        legacyService.VerifyNoOtherCalls();
    }
}
