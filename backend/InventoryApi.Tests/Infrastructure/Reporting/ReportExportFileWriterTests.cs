using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Reporting.Export;
using Inventory.Infrastructure.Reporting;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Reporting;

/// <summary>
/// Issue #306 moved the CSV/XLSX byte encoder out of <c>InventoryApi.Adapters.Export</c> into
/// <c>Inventory.Infrastructure.Reporting</c>, behind the Application-owned
/// <see cref="IReportExportFileWriter"/> port, so that ClosedXML leaves the API project with it.
/// The encoding itself had to stay byte-for-byte identical, which is why the first test pins the
/// exact payload rather than "a file was produced": every CSV cell is double-quoted, an embedded
/// quote is doubled, each row is terminated with <see cref="Environment.NewLine"/>, and the bytes
/// are UTF-8 with no byte-order mark.
///
/// The encoder is also the one place that must not add, derive or recompute a report value, so the
/// final test compares the two formats for one table: every XLSX cell holds the same text as the
/// corresponding CSV field, which is what makes the two downloads the same report.
/// </summary>
public class ReportExportFileWriterTests
{
    private static readonly IReportExportFileWriter Writer = new ReportExportFileWriter();

    private static ReportExportTable Table() => new(
        [
            ["Date", "Product", "Gross Sales", "Note"],
            ["2026-07-01", "Water \"600ml\"", "1.50", string.Empty],
            ["Totals", string.Empty, "1.50", "Provisional, 1 uncosted sale"],
        ],
        "Daily");

    [Fact]
    public void WriteCsv_quotes_every_cell_doubles_embedded_quotes_and_emits_utf8_without_a_bom()
    {
        var expected =
            "\"Date\",\"Product\",\"Gross Sales\",\"Note\"" + Environment.NewLine +
            "\"2026-07-01\",\"Water \"\"600ml\"\"\",\"1.50\",\"\"" + Environment.NewLine +
            "\"Totals\",\"\",\"1.50\",\"Provisional, 1 uncosted sale\"" + Environment.NewLine;

        var bytes = Writer.WriteCsv(Table());

        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        // A byte-order mark would corrupt the first header cell for a consumer that reads the file
        // as plain UTF-8, so its absence is part of the contract, not an incidental detail.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void WriteCsv_of_a_table_with_no_rows_is_empty()
    {
        // An empty report is a legitimate result (a filter that matches nothing), not an error, and
        // it must not become a stray blank line that a consumer reads as one empty record.
        Assert.Empty(Writer.WriteCsv(new ReportExportTable([], "Daily")));
    }

    [Fact]
    public void WriteXlsx_writes_the_tables_sheet_name_and_every_cell_in_reading_order()
    {
        using var stream = new MemoryStream(Writer.WriteXlsx(Table()));
        using var workbook = new XLWorkbook(stream);

        var sheet = Assert.Single(workbook.Worksheets);
        Assert.Equal("Daily", sheet.Name);
        Assert.Equal("Date", sheet.Cell(1, 1).GetString());
        Assert.Equal("Note", sheet.Cell(1, 4).GetString());
        Assert.Equal("Water \"600ml\"", sheet.Cell(2, 2).GetString());
        // Written as text, not reinterpreted as a number or a date: the rows arrive already
        // formatted by GetReportExportRows, and parsing them here would let the XLSX disagree with
        // the CSV and with the API response.
        Assert.Equal("1.50", sheet.Cell(2, 3).GetString());
        Assert.Equal("2026-07-01", sheet.Cell(2, 1).GetString());
        Assert.Equal("Provisional, 1 uncosted sale", sheet.Cell(3, 4).GetString());
    }

    [Fact]
    public void WriteXlsx_tolerates_rows_of_different_widths()
    {
        var ragged = new ReportExportTable([["A", "B", "C"], ["only one"]], "Ragged");

        using var stream = new MemoryStream(Writer.WriteXlsx(ragged));
        using var workbook = new XLWorkbook(stream);

        var sheet = workbook.Worksheet("Ragged");
        Assert.Equal("only one", sheet.Cell(2, 1).GetString());
        Assert.Equal(string.Empty, sheet.Cell(2, 2).GetString());
    }

    [Fact]
    public void Csv_and_xlsx_present_the_same_cells_of_the_same_table()
    {
        var table = Table();

        var csvLines = Encoding.UTF8.GetString(Writer.WriteCsv(table))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        using var stream = new MemoryStream(Writer.WriteXlsx(table));
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(table.SheetName);

        Assert.Equal(table.Rows.Count, csvLines.Length);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            // The expected CSV line is built from the table here instead of read back with a
            // parser, so a parser that mirrored a broken encoder could not make this pass.
            Assert.Equal(
                string.Join(",", table.Rows[r].Select(cell => $"\"{cell.Replace("\"", "\"\"")}\"")),
                csvLines[r]);

            for (var c = 0; c < table.Rows[r].Count; c++)
            {
                Assert.Equal(table.Rows[r][c], sheet.Cell(r + 1, c + 1).GetString());
            }
        }
    }
}
