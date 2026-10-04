using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Imports;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// Reads an uploaded Nayax transaction export into <see cref="NayaxSalesImportRow"/> facts behind
/// the Application's <see cref="INayaxSalesWorkbookReader"/> port (issue #301).
///
/// Workbook opening, the CSV-to-worksheet conversion and every cell-to-value conversion moved here
/// unchanged from <c>InventoryApi.Services.NayaxSalesWorkbook</c> and the parsing half of
/// <c>InventoryApi.Services.ImportService.ImportNayaxSalesFromExcelAsync</c>, including the
/// decisions imported financial data depends on:
/// <list type="bullet">
///   <item>Headers are matched on letters and digits only, case-insensitively, so
///   <c>Product Cost Price</c>, <c>ProductCostPrice</c> and <c>product_cost_price</c> are one
///   column; the transaction cost price is additionally accepted as <c>ProductCost</c> or
///   <c>CostPrice</c>, which are the spellings the Nayax export has been seen to use.</item>
///   <item>Numbers are read with <see cref="CultureInfo.InvariantCulture"/>, and an unparsable
///   number stays <c>null</c> rather than becoming zero - except the settlement value, which the
///   legacy import read as <c>0</c> when the column was absent.</item>
///   <item>A date cell is taken as a date, a numeric cell as an Excel serial date, and anything
///   else only as the export's own <c>d/M/yyyy h:mm:ss tt</c> text; an unrecognised instant stays
///   <c>null</c> so the row is skipped rather than imported at a guessed time.</item>
///   <item>A CSV is converted to a one-worksheet workbook of text cells, honouring quoted fields
///   and doubled quotes, which is why a CSV instant must match that exact text format.</item>
/// </list>
///
/// Unlike <see cref="FileSystemPendingReimbursementXmlSource"/>, a failure is deliberately *not*
/// translated into a plain answer here: an upload whose bytes are not a readable workbook must stay
/// a failure rather than be reported to the operator as an import of zero rows, and the exception
/// the reader raises keeps reaching <c>ImportsController.ImportNayaxSales</c>'s own handling exactly
/// as it did before this migration. Only a file that genuinely carries no data row reads as empty.
/// </summary>
public sealed class ClosedXmlNayaxSalesWorkbookReader : INayaxSalesWorkbookReader
{
    /// <inheritdoc />
    public IReadOnlyList<NayaxSalesImportRow> Read(Stream content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);

        using var workbook = Open(content, fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet is null)
            return [];
        var rows = worksheet.Rows().ToList();
        if (rows.Count == 0)
            return [];

        var headers = rows[0].Cells()
            .Where(cell => !cell.IsEmpty())
            .ToDictionary(
                cell => NormalizeHeader(cell.GetString()),
                cell => cell.Address.ColumnNumber,
                StringComparer.OrdinalIgnoreCase);

        var parsed = new List<NayaxSalesImportRow>(Math.Max(rows.Count - 1, 0));
        foreach (var row in rows.Skip(1))
            parsed.Add(new NayaxSalesImportRow(
                LongValue(Cell(row, headers, "TransactionID")),
                LongValue(Cell(row, headers, "MachineID")),
                DateValue(Cell(row, headers, "MachineAuthorizationTime")),
                IntValue(Cell(row, headers, "TransactionStatusId")),
                NullableLongValue(Cell(row, headers, "NayaxProductId")),
                TextValue(Cell(row, headers, "MachineName")),
                DecimalValue(Cell(row, headers, "SettlementValue")) ?? 0m,
                TextValue(Cell(row, headers, "PaymentMethod")),
                TextValue(Cell(row, headers, "ProductName")),
                DecimalValue(Cell(row, headers, "ProductCostPrice", "ProductCost", "CostPrice"))));

        return parsed;
    }

    /// <summary>
    /// Opens the upload as a workbook. A CSV becomes a single worksheet of text cells, which is
    /// what makes one set of cell conversions serve both formats.
    /// </summary>
    private static IXLWorkbook Open(Stream stream, bool isCsv)
    {
        if (!isCsv)
            return new XLWorkbook(stream);

        var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Sales");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var rowNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            var values = ParseCsvLine(line);
            for (var column = 0; column < values.Count; column++)
                worksheet.Cell(rowNumber, column + 1).Value = values[column];
            rowNumber++;
        }

        return workbook;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        values.Add(value.ToString());
        return values;
    }

    private static string NormalizeHeader(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The row's cell under the first of <paramref name="names"/> the header row carries.</summary>
    private static IXLCell? Cell(IXLRow row, IReadOnlyDictionary<string, int> headers, params string[] names)
    {
        foreach (var name in names)
            if (headers.TryGetValue(NormalizeHeader(name), out var index))
                return row.Cell(index);
        return null;
    }

    private static string? TextValue(IXLCell? cell) => cell is null || cell.IsEmpty() ? null : cell.GetString().Trim();

    private static long LongValue(IXLCell? cell) => long.TryParse(TextValue(cell), out var value) ? value : 0;

    private static long? NullableLongValue(IXLCell? cell) => long.TryParse(TextValue(cell), out var value) ? value : null;

    private static int? IntValue(IXLCell? cell) => int.TryParse(TextValue(cell), out var value) ? value : null;

    private static decimal? DecimalValue(IXLCell? cell) =>
        decimal.TryParse(TextValue(cell), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTime? DateValue(IXLCell? cell) =>
        cell is null || cell.IsEmpty() ? null :
        cell.Value.IsDateTime ? cell.Value.GetDateTime() :
        cell.Value.IsNumber ? DateTime.FromOADate(cell.Value.GetNumber()) :
        DateTime.TryParseExact(cell.GetString().Trim(), "d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
}
