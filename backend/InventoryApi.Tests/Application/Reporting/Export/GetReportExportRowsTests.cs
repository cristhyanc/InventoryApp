using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Export;

/// <summary>
/// Focused tests for the export use case that replaced the removed legacy reporting service's
/// export row-building: every row must come from the same authoritative report use case the API
/// response uses, never a re-derived value.
/// </summary>
public class GetReportExportRowsTests
{
    private static GetReportExportRows Sut(GetBookkeepingReport bookkeeping = null, GetTransactionSalesReport transactions = null,
        Inventory.Application.Reporting.Daily.GetDailyReport daily = null)
    {
        bookkeeping ??= new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        transactions ??= new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        daily ??= new Inventory.Application.Reporting.Daily.GetDailyReport(
            new InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider.SingleDay()));
        var reconciliation = new Inventory.Application.Reporting.Reconciliation.GetReconciliationReport(
            new InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitability = new Inventory.Application.Reporting.MachineProfitability.GetMachineProfitabilityReport(
            new InventoryApi.Tests.Application.Reporting.MachineProfitability.FakeMachineProfitabilityReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.MachineProfitability.FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitability = new Inventory.Application.Reporting.ProductProfitability.GetProductProfitabilityReport(
            new InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeProductProfitabilityReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeProductProfitabilityReportFactsProvider.Empty()));
        var gst = new Inventory.Application.Reporting.Gst.GetGstAccountingAid(bookkeeping,
            new InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider.Complete()));
        var dashboard = new Inventory.Application.Reporting.Dashboard.GetDashboardReport(bookkeeping,
            new InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeGetProductProfitabilityReport(
                new Inventory.Application.Reporting.ProductProfitability.ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new InventoryApi.Tests.Application.Reporting.Dashboard.FakeDashboardReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Dashboard.FakeDashboardReportFactsProvider.Complete()));

        return new GetReportExportRows(bookkeeping, daily, reconciliation, machineProfitability,
            productProfitability, gst, dashboard, transactions);
    }

    [Fact]
    public async Task Bookkeeping_export_reuses_the_same_authoritative_result_as_the_api_report()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(sales: 250m, cost: 90m)));
        var apiResult = await bookkeeping.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping",
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal("Report", table.SheetName);
        var header = table.Rows[0];
        var values = table.Rows[1];
        var grossSales = values[Array.IndexOf(header.ToArray(), "GrossSales")];
        Assert.Equal(apiResult.Sales.ToString(System.Globalization.CultureInfo.InvariantCulture), grossSales);
    }

    [Fact]
    public async Task Transactions_export_uses_the_transactions_sheet_name_and_the_unpaginated_result()
    {
        var row = FakeTransactionSalesReportFactsProvider.CompletedCardRow(transactionId: 7, sale: 42m);
        var facts = new TransactionSalesReportFacts([row], [], [], [], SiteMappingUnavailable: false);
        var transactions = new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(facts));

        var table = await Sut(transactions: transactions).Handle("transactions", new TransactionSalesFilterDto(), CancellationToken.None);

        Assert.Equal("Transactions", table.SheetName);
        Assert.Equal(2, table.Rows.Count);
        Assert.Contains("7", table.Rows[1]);
    }

    [Fact]
    public async Task Unsupported_transaction_report_name_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().Handle("not-a-report", new TransactionSalesFilterDto(), CancellationToken.None));
    }

    [Fact]
    public async Task Daily_export_header_detail_and_totals_rows_all_have_the_same_column_count()
    {
        var table = await Sut().Handle("daily", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var headerLength = table.Rows[0].Count;
        Assert.Equal(20, headerLength);
        Assert.All(table.Rows, row => Assert.Equal(headerLength, row.Count));
    }

    [Fact]
    public async Task Daily_detail_row_fee_gst_is_read_from_the_authoritative_processing_fee_result_not_derived_by_subtraction()
    {
        var processingFees = new NayaxProcessingFeeResult(4m, 0.5m, 4.4m, 0m, 0m, 0m, 0, DateTime.UtcNow, null);
        var daily = new Inventory.Application.Reporting.Daily.GetDailyReport(
            new InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider.SingleDay(processingFees: processingFees)));

        var table = await Sut(daily: daily).Handle("daily", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var header = table.Rows[0];
        var feeGstIndex = header.ToList().IndexOf("NayaxFeeGST");
        var detailRow = table.Rows[1];
        var totalsRow = table.Rows[2];

        Assert.Equal("0.5", detailRow[feeGstIndex]);
        Assert.Equal("0.5", totalsRow[feeGstIndex]);
        Assert.NotEqual((4.4m - 4m).ToString(System.Globalization.CultureInfo.InvariantCulture), detailRow[feeGstIndex]);
    }

    [Fact]
    public async Task Daily_totals_row_is_header_aligned_with_empty_fee_source_and_reconciliation_status()
    {
        var table = await Sut().Handle("daily", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var header = table.Rows[0].ToList();
        var totalsRow = table.Rows[^1];
        Assert.Equal("TOTAL", totalsRow[0]);
        Assert.Equal(string.Empty, totalsRow[header.IndexOf("FeeSource")]);
        Assert.Equal(string.Empty, totalsRow[header.IndexOf("ReconciliationStatus")]);
        Assert.Equal("80", totalsRow[header.IndexOf("ImportedReimbursement")]);
        Assert.Equal("78", totalsRow[header.IndexOf("NetReimbursement")]);
        Assert.Equal("4.4", totalsRow[header.IndexOf("NayaxFeeIncGST")]);
        Assert.Equal("4", totalsRow[header.IndexOf("NayaxFeeExGst")]);
    }
}
