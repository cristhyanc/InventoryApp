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

public class ReportsControllerReconciliationTests
{
    [Fact]
    public async Task Reconciliation_action_routes_through_the_application_use_case_not_the_legacy_service()
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
        var legacyService = new Mock<IReportingService>(MockBehavior.Strict);
        var controller = new ReportsController(legacyService.Object, bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase);

        var report = await controller.Reconciliation(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), 0.01m, CancellationToken.None);

        Assert.Equal(250m, report.NayaxReportedGrossCardSales);
        Assert.True(report.IsMatch);
        legacyService.VerifyNoOtherCalls();
    }
}
