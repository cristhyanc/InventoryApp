using System.Text;
using ClosedXML.Excel;
using Inventory.Infrastructure.Imports;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// The workbook/CSV reading half of the uploaded Nayax sales import (issue #301), behind the
/// Application's <c>INayaxSalesWorkbookReader</c> port. These lock the parsing decisions imported
/// financial data depends on and that moved here unchanged from
/// <c>InventoryApi.Services.NayaxSalesWorkbook</c> and the parsing half of
/// <c>ImportService.ImportNayaxSalesFromExcelAsync</c>: header spelling tolerance, the cost-price
/// aliases, invariant numbers, the three accepted instant forms, CSV quoting, and the difference
/// between a file that carries no data row and a file that is not readable at all.
/// </summary>
public class ClosedXmlNayaxSalesWorkbookReaderTests
{
    private static readonly DateTime Authorized = new(2026, 9, 2, 14, 30, 0);

    [Fact]
    public void A_real_workbook_is_read_with_its_typed_date_and_number_cells()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sales");
        Header(sheet, "TransactionID", "TransactionStatusId", "MachineID", "NayaxProductId",
            "SettlementValue", "PaymentMethod", "ProductName", "Product Cost Price", "MachineAuthorizationTime");
        sheet.Cell(2, 1).Value = 1001;
        sheet.Cell(2, 2).Value = 12;
        sheet.Cell(2, 3).Value = 7;
        sheet.Cell(2, 4).Value = 10;
        sheet.Cell(2, 5).Value = 3.25;
        sheet.Cell(2, 6).Value = "Card";
        sheet.Cell(2, 7).Value = "Snack";
        sheet.Cell(2, 8).Value = 1.1;
        sheet.Cell(2, 9).Value = Authorized;

        var row = Assert.Single(Read(workbook, "sales.xlsx"));

        Assert.Equal(1001, row.TransactionId);
        Assert.Equal(12, row.TransactionStatusId);
        Assert.Equal(7, row.MachineId);
        Assert.Equal(10, row.NayaxProductId);
        Assert.Equal(3.25m, row.SettlementValue);
        Assert.Equal("Card", row.PaymentMethod);
        Assert.Equal("Snack", row.ProductName);
        Assert.Equal(1.1m, row.NayaxProductCostPrice);
        Assert.Equal(Authorized, row.MachineAuthorizationTime);
    }

    /// <summary>
    /// An Excel serial date is a plain number in the cell, so it has to be recognised as an
    /// instant rather than skipped - otherwise a whole export saved that way would import as
    /// skipped rows.
    /// </summary>
    [Fact]
    public void A_numeric_cell_is_read_as_an_Excel_serial_date()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sales");
        Header(sheet, "TransactionID", "MachineID", "MachineAuthorizationTime");
        sheet.Cell(2, 1).Value = 1001;
        sheet.Cell(2, 2).Value = 7;
        sheet.Cell(2, 3).Value = Authorized.ToOADate();

        var row = Assert.Single(Read(workbook, "sales.xlsx"));

        Assert.Equal(Authorized, row.MachineAuthorizationTime);
    }

    [Theory]
    [InlineData("Product Cost Price")]
    [InlineData("ProductCostPrice")]
    [InlineData("product_cost_price")]
    [InlineData("Product Cost")]
    [InlineData("ProductCost")]
    [InlineData("Cost Price")]
    [InlineData("CostPrice")]
    public void Every_supported_transaction_cost_header_spelling_is_read(string header)
    {
        var row = Assert.Single(ReadCsv(
            $"TransactionID,MachineID,{header},MachineAuthorizationTime\n" +
            "1001,7,1.10,2/9/2026 2:30:00 PM"));

        Assert.Equal(1.10m, row.NayaxProductCostPrice);
    }

    /// <summary>
    /// A column the export does not carry must read as unknown rather than zero: a fabricated
    /// <c>0</c> transaction cost would be applied as a real historical cost of nothing.
    /// </summary>
    [Fact]
    public void An_absent_or_unparsable_cost_or_status_reads_as_unknown_rather_than_zero()
    {
        var rows = ReadCsv(
            "TransactionID,MachineID,TransactionStatusId,ProductCostPrice,MachineAuthorizationTime\n" +
            "1001,7,,,2/9/2026 2:30:00 PM\n" +
            "1002,7,not-a-status,not-a-cost,2/9/2026 2:30:00 PM");

        Assert.All(rows, row =>
        {
            Assert.Null(row.TransactionStatusId);
            Assert.Null(row.NayaxProductCostPrice);
        });
    }

    /// <summary>
    /// The one deliberate default the legacy import applied while parsing: an absent settlement
    /// value read as <c>0</c>, which the persisted non-nullable column requires.
    /// </summary>
    [Fact]
    public void An_absent_settlement_value_reads_as_zero()
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,MachineAuthorizationTime\n" +
            "1001,7,2/9/2026 2:30:00 PM"));

        Assert.Equal(0m, row.SettlementValue);
    }

    [Fact]
    public void An_unrecognised_instant_reads_as_null_so_the_row_can_be_skipped()
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,MachineAuthorizationTime\n" +
            "1001,7,2026-09-02T14:30:00Z"));

        Assert.Null(row.MachineAuthorizationTime);
    }

    /// <summary>
    /// A product name is the fallback half of product matching, so a quoted field containing the
    /// delimiter must not be split and a doubled quote must survive as one.
    /// </summary>
    [Fact]
    public void A_quoted_csv_field_keeps_its_commas_and_doubled_quotes()
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,ProductName,MachineAuthorizationTime\n" +
            "1001,7,\"Chips, large (\"\"party\"\" size)\",2/9/2026 2:30:00 PM"));

        Assert.Equal("Chips, large (\"party\" size)", row.ProductName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TransactionID,MachineID,MachineAuthorizationTime")]
    [InlineData("TransactionID,MachineID,MachineAuthorizationTime\n")]
    public void A_file_with_no_data_rows_reads_as_an_empty_import(string content)
    {
        Assert.Empty(ReadCsv(content));
    }

    /// <summary>
    /// A file whose bytes are not a readable workbook must stay a failure. Reporting it as an
    /// import of zero rows would tell an operator their transactions were imported when nothing
    /// was read at all.
    ///
    /// The reader deliberately does not translate it: the legacy import let the workbook library's
    /// <see cref="FileFormatException"/> propagate, and because that is not the
    /// <see cref="InvalidOperationException"/> <c>ImportsController.ImportNayaxSales</c> claims, a
    /// corrupt upload reaches <c>GlobalExceptionHandler</c> as a logged, generic <c>500</c> - the
    /// behaviour this migration preserves rather than changes.
    /// </summary>
    [Fact]
    public void A_malformed_workbook_upload_fails_instead_of_reading_as_empty()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("this is not a workbook"));

        var thrown = Assert.Throws<FileFormatException>(
            () => new ClosedXmlNayaxSalesWorkbookReader().Read(content, "sales.xlsx"));

        // Not an InvalidOperationException, so the controller's 400 handling never claims it and a
        // corrupt upload is not answered as a validation message.
        Assert.IsNotAssignableFrom<InvalidOperationException>(thrown);
    }

    /// <summary>
    /// A CSV, unlike a workbook, is readable whatever it contains: every line parses as text, so
    /// unusable content surfaces as rows the import then counts as skipped rather than as a
    /// read failure.
    /// </summary>
    [Fact]
    public void A_malformed_csv_upload_reads_as_rows_with_no_identity()
    {
        var rows = ReadCsv("this is not a sales export\nnor is this\nor this");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(0, row.TransactionId);
            Assert.Equal(0, row.MachineId);
            Assert.Null(row.MachineAuthorizationTime);
        });
    }

    private static void Header(IXLWorksheet sheet, params string[] headers)
    {
        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).Value = headers[column];
    }

    private static IReadOnlyList<Inventory.Application.Imports.NayaxSalesImportRow> Read(
        IXLWorkbook workbook, string fileName)
    {
        using var content = new MemoryStream();
        workbook.SaveAs(content);
        content.Position = 0;
        return new ClosedXmlNayaxSalesWorkbookReader().Read(content, fileName);
    }

    private static IReadOnlyList<Inventory.Application.Imports.NayaxSalesImportRow> ReadCsv(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return new ClosedXmlNayaxSalesWorkbookReader().Read(stream, "sales.csv");
    }
}
