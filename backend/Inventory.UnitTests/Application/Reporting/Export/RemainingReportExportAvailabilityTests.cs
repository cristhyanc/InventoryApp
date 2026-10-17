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
using Inventory.Domain.Reporting.Availability;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Dashboard;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.MachineProfitability;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
using InventoryApi.Tests.Application.Reporting.Shared;
using InventoryApi.Tests.Application.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Export;

/// <summary>
/// Issue #731: a downloaded report carries the same availability flags as the API report it came
/// from, for every remaining sales-based report.
///
/// <para>A CSV or XLSX file is read away from the application, often months later and often pasted
/// straight into a working paper, so it is the surface where an unlabelled <c>0</c> does the most
/// damage: nothing on the page says whether that zero was measured. Each test here asserts both
/// halves of the rule - the state is reported in its own column, and a zero nothing measured is
/// replaced by the state rather than printed as <c>0</c> - and that a complete period still prints
/// its real zero.</para>
/// </summary>
public class RemainingReportExportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);
    private static readonly ReportingFilterDto Filter = new(From, To);

    private const string Missing = nameof(ReportAvailabilityState.Missing);
    private const string Complete = nameof(ReportAvailabilityState.Complete);

    private static string Cell(ReportExportTable table, string column, int row = 1)
    {
        var index = table.Rows[0].ToList().IndexOf(column);
        Assert.True(index >= 0, $"The export has no '{column}' column.");
        return table.Rows[row][index];
    }

    private static GetReportExportRows Sut(ReportCoverageFacts coverage)
    {
        var bookkeepingFacts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SalesCoverage = coverage.SalesCoverage,
            SettlementCoverage = coverage.SettlementCoverage,
            ContainsSalesRows = coverage.ContainsSalesRows,
            ImportedContainsRows = coverage.ContainsSettlementRows,
        };
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(bookkeepingFacts));
        var daily = new GetDailyReport(new FakeDailyReportFactsProvider(
            FakeDailyReportFactsProvider.SingleDay(coverage: coverage)));
        var reconciliation = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(
            FakeReconciliationReportFactsProvider.SinglePeriod(coverage: coverage)));
        var machines = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(
            FakeMachineProfitabilityReportFactsProvider.SingleMachine(coverage: coverage)));
        var products = new GetProductProfitabilityReport(
            new FakeProductProfitabilityReportFactsProvider(
                FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(coverage: coverage)),
            FakeProductPurchaseCostFactsProvider.Empty());
        var gst = new GetGstAccountingAid(bookkeeping,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboard = new GetDashboardReport(bookkeeping,
            new FakeGetProductProfitabilityReport(
                new ProductProfitabilityReportDto(From, To, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var transactions = new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(
            FakeTransactionSalesReportFactsProvider.Facts(
                [FakeTransactionSalesReportFactsProvider.CompletedCardRow(transactionDate: From.AddDays(1))],
                coverage: coverage)));

        return new GetReportExportRows(bookkeeping, daily, reconciliation, machines, products, gst, dashboard, transactions);
    }

    [Fact]
    public async Task Daily_export_reports_every_state_and_replaces_an_unmeasured_zero()
    {
        var table = await Sut(TestReportCoverage.Missing()).Handle("daily", Filter, CancellationToken.None);

        Assert.Equal(Missing, Cell(table, "SalesDataAvailability"));
        Assert.Equal(Missing, Cell(table, "FeeDataAvailability"));
        Assert.Equal(Missing, Cell(table, "CombinedDataAvailability"));
        // The totals row carries the state too, because a downloaded row is read on its own.
        Assert.Equal(Missing, Cell(table, "SalesDataAvailability", row: table.Rows.Count - 1));
    }

    [Fact]
    public async Task Daily_export_prints_a_measured_zero_as_a_zero_when_coverage_is_complete()
    {
        var daily = new GetDailyReport(new FakeDailyReportFactsProvider(
            FakeDailyReportFactsProvider.SingleDay(
                grossSales: 0m, transactionCount: 0, partialCostOfGoods: 0m, cardSales: 0m, cashSales: 0m,
                completedTransactionCount: 0, importedReimbursement: 0m, netReimbursement: 0m)));
        var table = await SutWith(daily).Handle("daily", Filter, CancellationToken.None);

        Assert.Equal(Complete, Cell(table, "SalesDataAvailability"));
        Assert.Equal("0", Cell(table, "GrossSales"));
    }

    [Fact]
    public async Task Daily_export_keeps_a_real_partial_amount_visible_beside_its_state()
    {
        var table = await Sut(TestReportCoverage.StaleSales(new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc)))
            .Handle("daily", Filter, CancellationToken.None);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), Cell(table, "SalesDataAvailability"));
        Assert.Equal("2026-07-18 06:00:00", Cell(table, "SalesDataAsOfUtc"));
        Assert.Equal("100", Cell(table, "GrossSales"));
    }

    [Fact]
    public async Task Reconciliation_export_reports_every_state_and_never_prints_an_unmeasured_difference_as_zero()
    {
        var table = await Sut(TestReportCoverage.Missing()).Handle("reconciliation", Filter, CancellationToken.None);

        Assert.Equal(Missing, Cell(table, "SalesDataAvailability"));
        Assert.Equal(Missing, Cell(table, "SettlementDataAvailability"));
        Assert.Equal(Missing, Cell(table, "CombinedDataAvailability"));
        Assert.Equal(Missing, Cell(table, "GrossDifference"));
        Assert.Equal(Missing, Cell(table, "CountDifference"));
    }

    [Fact]
    public async Task Machine_profitability_export_reports_every_state()
    {
        var table = await Sut(TestReportCoverage.Missing()).Handle("machine-profitability", Filter, CancellationToken.None);

        Assert.Equal(Missing, Cell(table, "SalesDataAvailability"));
        Assert.Equal(Missing, Cell(table, "FeeDataAvailability"));
        Assert.Equal(Missing, Cell(table, "CombinedDataAvailability"));
    }

    [Fact]
    public async Task Product_profitability_export_reports_the_one_state_that_governs_it()
    {
        var table = await Sut(TestReportCoverage.Missing()).Handle("product-profitability", Filter, CancellationToken.None);

        Assert.Equal(Missing, Cell(table, "SalesDataAvailability"));
        Assert.Equal(string.Empty, Cell(table, "SalesDataAsOfUtc"));
        // No settlement column at all: nothing in this report comes from a settlement import.
        Assert.DoesNotContain("FeeDataAvailability", table.Rows[0]);
    }

    [Fact]
    public async Task Dashboard_export_reports_the_states_the_bookkeeping_report_decided()
    {
        var table = await Sut(TestReportCoverage.Missing()).Handle("dashboard", Filter, CancellationToken.None);

        Assert.Equal(Missing, Cell(table, "SalesDataAvailability"));
        Assert.Equal(Missing, Cell(table, "FeeDataAvailability"));
        Assert.Equal(Missing, Cell(table, "CombinedDataAvailability"));
        // A real non-zero amount is partial data and stays exactly as calculated, beside the state
        // columns that say it may not be all of it.
        Assert.Equal("100", Cell(table, "Sales"));
        // The counts over those sales are not a catalogue size and carry the same state, so a
        // machine count of zero nothing measured never reads as "this business has no machines".
        Assert.Equal("2", Cell(table, "MachineCount"));
    }

    [Fact]
    public async Task Transactions_export_reports_the_state_beside_every_stored_row()
    {
        var table = await Sut(TestReportCoverage.Missing())
            .Handle("transactions", new TransactionSalesFilterDto(From, To), CancellationToken.None);

        Assert.Equal(Missing, Cell(table, "SalesDataAvailability"));
        // A stored transaction is a measurement in itself, so its own amounts stay exact.
        Assert.Equal("10", Cell(table, "Sale"));
    }

    [Fact]
    public async Task Every_remaining_export_keeps_its_header_and_rows_the_same_width()
    {
        var sut = Sut(TestReportCoverage.Missing());

        foreach (var report in new[] { "daily", "reconciliation", "machine-profitability", "product-profitability", "dashboard" })
        {
            var table = await sut.Handle(report, Filter, CancellationToken.None);
            var width = table.Rows[0].Count;
            Assert.All(table.Rows, row => Assert.Equal(width, row.Count));
        }

        var transactions = await sut.Handle("transactions", new TransactionSalesFilterDto(From, To), CancellationToken.None);
        var transactionWidth = transactions.Rows[0].Count;
        Assert.All(transactions.Rows, row => Assert.Equal(transactionWidth, row.Count));
    }

    /// <summary>The complete-coverage composition, with one report replaced.</summary>
    private static GetReportExportRows SutWith(GetDailyReport daily)
    {
        var bookkeeping = new GetBookkeepingReport(
            new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var reconciliation = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(
            FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machines = new GetMachineProfitabilityReport(new FakeMachineProfitabilityReportFactsProvider(
            FakeMachineProfitabilityReportFactsProvider.Empty()));
        var products = new GetProductProfitabilityReport(
            new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()),
            FakeProductPurchaseCostFactsProvider.Empty());
        var gst = new GetGstAccountingAid(bookkeeping,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboard = new GetDashboardReport(bookkeeping,
            new FakeGetProductProfitabilityReport(
                new ProductProfitabilityReportDto(From, To, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var transactions = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));

        return new GetReportExportRows(bookkeeping, daily, reconciliation, machines, products, gst, dashboard, transactions);
    }
}
