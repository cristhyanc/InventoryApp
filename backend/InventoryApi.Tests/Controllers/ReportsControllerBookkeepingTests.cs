using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
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
        var legacyService = new Mock<IReportingService>(MockBehavior.Strict);
        var controller = new ReportsController(legacyService.Object, useCase, dailyUseCase, reconciliationUseCase);

        var report = await controller.Bookkeeping(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal(250m, report.Sales);
        Assert.Equal(90m, report.CostOfGoods);
        legacyService.VerifyNoOtherCalls();
    }
}
