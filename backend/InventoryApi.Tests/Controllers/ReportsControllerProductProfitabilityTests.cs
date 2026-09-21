using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.MachineProfitability;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ReportsControllerProductProfitabilityTests
{
    [Fact]
    public async Task Product_profitability_action_routes_through_the_application_use_case_not_the_legacy_service()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 7, productName: "Soda", sales: 60m, cost: 20m);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));
        var bookkeepingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var reconciliationUseCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var gstUseCase = new GetGstAccountingAid(bookkeepingUseCase, new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var legacyService = new Mock<IReportingService>(MockBehavior.Strict);
        var controller = new ReportsController(legacyService.Object, bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, useCase, gstUseCase);

        var report = await controller.ProductProfitability(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(7, row.ProductId);
        Assert.Equal("Soda", row.ProductName);
        Assert.Equal(60m, row.Sales);
        Assert.Equal(40m, row.GrossProfit);
        legacyService.VerifyNoOtherCalls();
    }
}
