using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Reporting.Availability;
using Inventory.Infrastructure.Reporting;
using InventoryApi.Tests.Application.Reporting.Export;
using InventoryApi.Tests.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Reporting;

/// <summary>
/// Issue #731: the availability state of a report with no data rows reaches the downloaded file, in
/// both formats.
///
/// <para>Transaction sales, product profitability and machine profitability produce a row only where
/// sales exist, and their state columns live on those rows. An empty result therefore downloaded as
/// column headings and nothing else, where a <c>Missing</c> or <c>Unverified</c> period is
/// indistinguishable from a <c>Complete</c> one that genuinely sold nothing - the single confusion
/// this model exists to prevent, and worst in a file read months later away from the application.
/// These tests assert the whole path, from the use-case result through
/// <see cref="GetReportExportRows"/> to the bytes <see cref="ReportExportFileWriter"/> encodes,
/// because the export-table representation alone does not prove what a reader opens.</para>
/// </summary>
public class EmptyReportExportAvailabilityFileTests
{
    private static readonly IReportExportFileWriter Writer = new ReportExportFileWriter();

    private static readonly ReportingFilterDto Filter =
        new(EmptyReportExportComposition.From, EmptyReportExportComposition.To);

    [Theory]
    [InlineData(nameof(ReportAvailabilityState.Missing))]
    [InlineData(nameof(ReportAvailabilityState.Unverified))]
    [InlineData(nameof(ReportAvailabilityState.Complete))]
    public async Task An_empty_transactions_download_states_its_sales_availability(string state)
    {
        var table = await EmptyReportExportComposition.Empty(TestReportCoverage.Of(state)).Handle(
            "transactions",
            new TransactionSalesFilterDto(EmptyReportExportComposition.From, EmptyReportExportComposition.To),
            CancellationToken.None);

        AssertBothFormatsCarry(table, state, trailingStateColumns: 2, "SalesDataAvailability");
    }

    [Theory]
    [InlineData(nameof(ReportAvailabilityState.Missing))]
    [InlineData(nameof(ReportAvailabilityState.Unverified))]
    [InlineData(nameof(ReportAvailabilityState.Complete))]
    public async Task An_empty_product_profitability_download_states_its_sales_availability(string state)
    {
        var table = await EmptyReportExportComposition.Empty(TestReportCoverage.Of(state))
            .Handle("product-profitability", Filter, CancellationToken.None);

        AssertBothFormatsCarry(table, state, trailingStateColumns: 2, "SalesDataAvailability");
    }

    [Theory]
    [InlineData(nameof(ReportAvailabilityState.Missing))]
    [InlineData(nameof(ReportAvailabilityState.Unverified))]
    [InlineData(nameof(ReportAvailabilityState.Complete))]
    public async Task An_empty_machine_profitability_download_states_every_availability(string state)
    {
        var table = await EmptyReportExportComposition.Empty(TestReportCoverage.Of(state))
            .Handle("machine-profitability", Filter, CancellationToken.None);

        AssertBothFormatsCarry(table, state, trailingStateColumns: 5,
            "SalesDataAvailability", "FeeDataAvailability", "CombinedDataAvailability");
    }

    [Fact]
    public async Task A_download_with_rows_carries_no_metadata_row()
    {
        // The metadata row is the empty result's only way to speak; a report that has rows keeps
        // exactly the shape it already had, with its states repeated on each data row.
        var table = await EmptyReportExportComposition.WithOneMachine(TestReportCoverage.Missing())
            .Handle("machine-profitability", Filter, CancellationToken.None);

        var csvLines = CsvLines(table);
        Assert.Equal(2, csvLines.Length);
        Assert.DoesNotContain(ReportExportTable.NoRowsLabel, csvLines[1]);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), CellAt(table, csvLines[1], "SalesDataAvailability"));
    }

    /// <summary>
    /// The downloaded file holds exactly a header and one marked metadata row; that row carries the
    /// state in each of the report's availability columns and nothing anywhere else; and the CSV and
    /// the XLSX say the same thing, because both encode the one table the use case built.
    /// </summary>
    private static void AssertBothFormatsCarry(
        ReportExportTable table, string state, int trailingStateColumns, params string[] stateColumns)
    {
        var header = table.Rows[0];
        var csvLines = CsvLines(table);
        Assert.Equal(2, csvLines.Length);

        var csvCells = csvLines[1].Split(',');
        Assert.Equal(header.Count, csvCells.Length);
        Assert.Equal($"\"{ReportExportTable.NoRowsLabel}\"", csvCells[0]);

        using var stream = new MemoryStream(Writer.WriteXlsx(table));
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(table.SheetName);
        Assert.Equal(ReportExportTable.NoRowsLabel, sheet.Cell(2, 1).GetString());
        Assert.Equal(2, sheet.LastRowUsed()!.RowNumber());

        foreach (var column in stateColumns)
        {
            var index = IndexOf(header, column);
            Assert.Equal($"\"{state}\"", csvCells[index]);
            Assert.Equal(state, sheet.Cell(2, index + 1).GetString());
        }

        // Nothing but the label and the trailing state columns: no transaction, product or machine
        // is invented to carry the metadata.
        for (var i = 1; i < header.Count - trailingStateColumns; i++)
        {
            Assert.Equal("\"\"", csvCells[i]);
            Assert.Equal(string.Empty, sheet.Cell(2, i + 1).GetString());
        }
    }

    private static string[] CsvLines(ReportExportTable table) =>
        Encoding.UTF8.GetString(Writer.WriteCsv(table))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The value one CSV line holds in a named column, with the encoder's quotes removed.</summary>
    private static string CellAt(ReportExportTable table, string csvLine, string column) =>
        csvLine.Split(',')[IndexOf(table.Rows[0], column)].Trim('"');

    private static int IndexOf(IReadOnlyList<string> header, string column)
    {
        var index = header.ToList().IndexOf(column);
        Assert.True(index >= 0, $"The export has no '{column}' column.");
        return index;
    }
}
