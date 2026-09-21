using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.MachineProfitability;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ReportsControllerMachineProfitabilityTests
{
    [Fact]
    public async Task Machine_profitability_action_routes_through_the_application_use_case_not_the_legacy_service()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(machineId: 42, machineName: "Alpha", sales: 250m, cost: 90m);
        var useCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts));
        var bookkeepingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(FakeDailyReportFactsProvider.SingleDay()));
        var reconciliationUseCase = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var productProfitabilityUseCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()));
        var legacyService = new Mock<IReportingService>(MockBehavior.Strict);
        var controller = new ReportsController(legacyService.Object, bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            useCase, productProfitabilityUseCase);

        var report = await controller.MachineProfitability(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(42, row.MachineId);
        Assert.Equal("Alpha", row.MachineName);
        Assert.Equal(250m, row.Sales);
        Assert.Equal(90m, row.CostOfGoods);
        legacyService.VerifyNoOtherCalls();
    }
}
