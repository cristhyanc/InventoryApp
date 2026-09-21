using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.MachineProfitability;

public class GetMachineProfitabilityReportTests
{
    [Fact]
    public async Task Resolves_the_requested_range_and_machine_filter_and_forwards_them_to_the_port()
    {
        var provider = new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty());
        var useCase = new GetMachineProfitabilityReport(provider);

        await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Equal((new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), (long?)10), provider.LastRequest);
    }

    [Fact]
    public async Task Complete_machine_reports_gross_and_direct_profit()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(
            machineId: 10, sales: 100m, cost: 40m, cardSales: 80m, cashSales: 20m,
            commissionDue: 5m, operatingExpenses: 3m);
        var useCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(10, row.MachineId);
        Assert.Equal(80m, row.CardSales);
        Assert.Equal(20m, row.CashSales);
        Assert.Equal(60m, row.GrossProfit);
        Assert.NotNull(row.DirectProfit);
        Assert.DoesNotContain(report.DataQuality.Notes!, x => x.Contains("no persisted COGS") || x.Contains("processing fee rate") || x.Contains("Commission configuration"));
    }

    [Fact]
    public async Task Incomplete_cogs_reports_null_profit_and_a_quality_note()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(isCogsComplete: false);
        var useCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.GrossProfit);
        Assert.Null(row.DirectProfit);
        Assert.Null(row.DirectMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("no persisted COGS"));
    }

    [Fact]
    public async Task Missing_fee_rate_makes_direct_profit_unavailable_and_adds_a_quality_note()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(hasMissingFeeRates: true);
        var useCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.NotNull(row.GrossProfit);
        Assert.Null(row.DirectProfit);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("no effective Nayax processing fee rate"));
    }

    [Fact]
    public async Task Incomplete_commission_makes_direct_profit_unavailable_and_adds_a_quality_note()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine(commissionComplete: false);
        var useCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.DirectProfit);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
    }

    [Fact]
    public async Task Commission_warnings_from_facts_are_appended_to_quality_notes()
    {
        var facts = FakeMachineProfitabilityReportFactsProvider.SingleMachine() with
        {
            CommissionWarnings = ["Overlapping commission agreements cover one or more sales for the selected machine."]
        };
        var useCase = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("Overlapping commission agreements"));
    }
}
