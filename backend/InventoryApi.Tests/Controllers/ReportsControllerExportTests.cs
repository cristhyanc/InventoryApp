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
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The export endpoint's transport contract (issue #306). Byte encoding moved behind the
/// Application-owned <see cref="IReportExportFileWriter"/> port, implemented in
/// <c>Inventory.Infrastructure</c>, so the controller no longer calls a static API-owned writer.
/// What a client sees had to stay identical, and these tests pin exactly that: which port method
/// the requested format selects, the two content types, the downloaded file names, and that the
/// returned bytes are the writer's own output rather than anything the controller re-encodes.
/// They also pin that the transaction report keeps its own filter contract, since it is the one
/// report whose rows come from a different filter type.
/// </summary>
public class ReportsControllerExportTests
{
    private sealed class RecordingReportExportFileWriter : IReportExportFileWriter
    {
        public List<string> Calls { get; } = [];
        public ReportExportTable? LastTable { get; private set; }

        public byte[] WriteCsv(ReportExportTable table)
        {
            Calls.Add(nameof(WriteCsv));
            LastTable = table;
            return [1, 2, 3];
        }

        public byte[] WriteXlsx(ReportExportTable table)
        {
            Calls.Add(nameof(WriteXlsx));
            LastTable = table;
            return [4, 5, 6, 7];
        }
    }

    private static ReportsController Controller(IReportExportFileWriter writer)
    {
        var bookkeepingUseCase = new GetBookkeepingReport(
            new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var dailyUseCase = new GetDailyReport(new FakeDailyReportFactsProvider(
            FakeDailyReportFactsProvider.SingleDay(grossSales: 250m, partialCostOfGoods: 90m)));
        var reconciliationUseCase = new GetReconciliationReport(
            new FakeReconciliationReportFactsProvider(FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitabilityUseCase = new GetMachineProfitabilityReport(
            new FakeMachineProfitabilityReportFactsProvider(FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitabilityUseCase = new GetProductProfitabilityReport(
            new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty()),
            FakeProductPurchaseCostFactsProvider.Empty());
        var gstUseCase = new GetGstAccountingAid(bookkeepingUseCase,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboardUseCase = new GetDashboardReport(bookkeepingUseCase,
            new FakeGetProductProfitabilityReport(
                new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var getTransactionSalesReport = new GetTransactionSalesReport(
            new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        var getReportExportRows = new GetReportExportRows(bookkeepingUseCase, dailyUseCase, reconciliationUseCase,
            machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase, dashboardUseCase,
            getTransactionSalesReport);

        return new ReportsController(getReportExportRows, writer, bookkeepingUseCase, dailyUseCase,
            reconciliationUseCase, machineProfitabilityUseCase, productProfitabilityUseCase, gstUseCase,
            dashboardUseCase, getTransactionSalesReport);
    }

    [Theory]
    [InlineData("csv", "text/csv", "daily.csv")]
    [InlineData("CSV", "text/csv", "daily.csv")]
    public async Task Csv_export_returns_the_writers_csv_bytes_with_the_csv_content_type_and_file_name(
        string format, string expectedContentType, string expectedFileName)
    {
        var writer = new RecordingReportExportFileWriter();

        var result = await Controller(writer).Export("daily", format,
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([nameof(IReportExportFileWriter.WriteCsv)], writer.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
        Assert.Equal(expectedContentType, file.ContentType);
        Assert.Equal(expectedFileName, file.FileDownloadName);
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("XLSX")]
    public async Task Xlsx_export_returns_the_writers_xlsx_bytes_with_the_spreadsheet_content_type(string format)
    {
        var writer = new RecordingReportExportFileWriter();

        var result = await Controller(writer).Export("daily", format,
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([nameof(IReportExportFileWriter.WriteXlsx)], writer.Calls);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, file.FileContents);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        // Lower-cased regardless of how the caller spelled the format, as before.
        Assert.Equal("daily.xlsx", file.FileDownloadName);
    }

    [Fact]
    public async Task An_unsupported_format_is_rejected_before_any_report_is_built_or_encoded()
    {
        var writer = new RecordingReportExportFileWriter();

        var result = await Controller(writer).Export("daily", "pdf",
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(writer.Calls);
    }

    [Theory]
    [InlineData("transactions")]
    [InlineData("transaction-sales")]
    public async Task The_transaction_report_is_encoded_from_its_own_filter_contract(string report)
    {
        var writer = new RecordingReportExportFileWriter();

        var result = await Controller(writer).Export(report, "csv", null,
            new TransactionSalesFilterDto(), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal($"{report}.csv", file.FileDownloadName);
        Assert.Equal("Transactions", writer.LastTable!.SheetName);
    }
}
