using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Daily;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ReportsControllerDailyTests
{
    [Fact]
    public async Task Daily_action_routes_through_the_application_use_case_not_the_legacy_service()
    {
        var facts = FakeDailyReportFactsProvider.SingleDay(grossSales: 250m, partialCostOfGoods: 90m);
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(facts));
        var bookkeepingUseCase = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var legacyService = new Mock<IReportingService>(MockBehavior.Strict);
        var controller = new ReportsController(legacyService.Object, bookkeepingUseCase, dailyUseCase);

        var report = await controller.Daily(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(250m, row.GrossSales);
        Assert.Equal(90m, row.CostOfGoods);
        legacyService.VerifyNoOtherCalls();
    }
}
