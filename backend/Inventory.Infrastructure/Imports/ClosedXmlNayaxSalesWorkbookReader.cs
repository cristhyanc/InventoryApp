using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;

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
///   number stays <c>null</c> rather than becoming zero. Since issue #1298 that holds for the
///   settlement value too, which this reader used to default to <c>0</c> when the column was absent;
///   the status and the settlement value additionally report which of the three cases the cell was -
///   nothing stated, a readable value, or a non-blank value this reader cannot read.</item>
///   <item>A date cell is taken as a date, a numeric cell as an Excel serial date, and anything
///   else only as the export's own <c>d/M/yyyy h:mm:ss tt</c> text; an unrecognised instant stays
///   <c>null</c> so the row is skipped rather than imported at a guessed time.</item>
///   <item>An <c>AuthorizationDateTimeGMT</c> column, the authoritative sale instant wherever the
///   export carries one (issue #380), is additionally read in the ISO/offset-carrying form a GMT
///   column is written in, and is normalized to UTC once. The machine-local
///   <c>MachineAuthorizationTime</c> column's own parsing is unchanged, offsets included: an offset
///   there would contradict what that field means in the Nayax contract.</item>
///   <item>A CSV is converted to a one-worksheet workbook of text cells, honouring quoted fields
///   and doubled quotes, which is why a CSV instant must match that exact text format.</item>
///   <item><b>The caller's limits are enforced here, while the bytes are being read</b> (issue
///   #1300), because that is the only place they can be: by the time a use case could count rows,
///   the memory is already spent. A CSV is abandoned as soon as one line past the row limit is
///   found, so the rows beyond it are never converted to cells; an XLSX's sheet data is measured
///   from the package's own entry sizes <em>before</em> the workbook is opened, because a
///   compressed archive is read into memory in one step that cannot be interrupted. A file beyond
///   either limit is answered as <see cref="NayaxSalesWorkbookReadResult.BeyondLimits"/>, with
///   nothing parsed.</item>
/// </list>
///
/// Unlike <see cref="ZipReimbursementArchiveReader"/>, a failure is deliberately *not*
/// translated into a plain answer here: an upload whose bytes are not a readable workbook must stay
/// a failure rather than be reported to the operator as an import of zero rows, and the exception
/// the reader raises keeps reaching <c>ImportsController.ImportNayaxSales</c>'s own handling exactly
/// as it did before this migration. Only a file that genuinely carries no data row reads as empty.
/// </summary>
public sealed class ClosedXmlNayaxSalesWorkbookReader : INayaxSalesWorkbookReader
{
    /// <inheritdoc />
    public NayaxSalesWorkbookReadResult Read(Stream content, string fileName, NayaxSalesFileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(limits);

        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            // null means the conversion stopped at the row limit, having built nothing past it.
            using var converted = OpenCsv(content, limits);
            return converted is null ? NayaxSalesWorkbookReadResult.BeyondLimits : Parse(converted, limits);
        }

        // The package's entry sizes are read before the workbook is opened (issue #1300). A
        // seekable stream is what an uploaded file gives; anything else is buffered first, because
        // the sizes cannot be read from a stream that has already been consumed.
        using var buffered = content.CanSeek ? null : CopyToMemory(content);
        var package = buffered ?? content;
        if (SheetDataExceeds(package, limits))
            return NayaxSalesWorkbookReadResult.BeyondLimits;

        using var workbook = new XLWorkbook(package);
        return Parse(workbook, limits);
    }

    /// <summary>
    /// Every data row of an opened workbook, in file order, under the caller's row limit. The cell
    /// conversions below are unchanged; what issue #1300 added is the refusal above the limit, made
    /// before a single cell is converted.
    /// </summary>
    private static NayaxSalesWorkbookReadResult Parse(IXLWorkbook workbook, NayaxSalesFileLimits limits)
    {
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet is null)
            return NayaxSalesWorkbookReadResult.Empty;
        var rows = worksheet.Rows().ToList();
        if (rows.Count == 0)
            return NayaxSalesWorkbookReadResult.Empty;

        // The header row is row 1 and is not a data row, which is what the limit counts. A CSV has
        // already stopped at the limit by now; an XLSX could only be counted once it was loaded.
        if (limits.MaxDataRows is int maxDataRows && rows.Count - 1 > maxDataRows)
            return NayaxSalesWorkbookReadResult.BeyondLimits;

        var headers = rows[0].Cells()
            .Where(cell => !cell.IsEmpty())
            .ToDictionary(
                cell => NormalizeHeader(cell.GetString()),
                cell => cell.Address.ColumnNumber,
                StringComparer.OrdinalIgnoreCase);

        var parsed = new List<NayaxSalesImportRow>(Math.Max(rows.Count - 1, 0));
        foreach (var row in rows.Skip(1))
        {
            var (gmt, gmtInput) = GmtValue(Cell(row, headers, "AuthorizationDateTimeGMT"));
            var (status, statusInput) = IntInput(Cell(row, headers, "TransactionStatusId"));
            var (settlement, settlementInput) = DecimalInput(Cell(row, headers, "SettlementValue"));
            parsed.Add(new NayaxSalesImportRow(
                LongValue(Cell(row, headers, "TransactionID")),
                LongValue(Cell(row, headers, "MachineID")),
                DateValue(Cell(row, headers, "MachineAuthorizationTime")),
                status,
                NullableLongValue(Cell(row, headers, "NayaxProductId")),
                TextValue(Cell(row, headers, "MachineName")),
                settlement,
                TextValue(Cell(row, headers, "PaymentMethod")),
                TextValue(Cell(row, headers, "ProductName")),
                DecimalValue(Cell(row, headers, "ProductCostPrice", "ProductCost", "CostPrice")),
                gmt,
                gmtInput,
                statusInput,
                settlementInput,
                row.RowNumber()));
        }

        return new NayaxSalesWorkbookReadResult(parsed, false);
    }

    /// <summary>
    /// Converts a CSV upload into a single worksheet of text cells, which is what makes one set of
    /// cell conversions serve both formats, or answers <see langword="null"/> when the file carries
    /// more data rows than the caller allows.
    ///
    /// <para>The limit is applied line by line rather than to the finished worksheet (issue #1300):
    /// a 114,250-row export allocated about a gigabyte of cells on its way to being counted, so the
    /// conversion stops at the first line past the limit and discards what it had built. Everything
    /// else about the conversion - the quoting rules, the one worksheet, the text cells - is
    /// unchanged, so a file inside the limit is read exactly as it was.</para>
    /// </summary>
    private static IXLWorkbook? OpenCsv(Stream stream, NayaxSalesFileLimits limits)
    {
        var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Sales");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var rowNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            // Row 1 is the header, so the data rows written so far are rowNumber - 1: this line is
            // one too many exactly when rowNumber already counts past the limit's last data row.
            if (limits.MaxDataRows is int maxDataRows && rowNumber > maxDataRows + 1)
            {
                workbook.Dispose();
                return null;
            }

            var values = ParseCsvLine(line);
            for (var column = 0; column < values.Count; column++)
                worksheet.Cell(rowNumber, column + 1).Value = values[column];
            rowNumber++;
        }

        return workbook;
    }

    /// <summary>
    /// Whether a compressed workbook's sheet data expands past the caller's limit, measured from the
    /// package's own entry sizes and <em>before</em> the workbook is opened (issue #1300).
    ///
    /// <para>What is summed is the worksheet parts and the shared string table - the two parts that
    /// hold the rows, and therefore the two whose expansion is what loading the file costs. The
    /// uncompressed size is the archive's own declared entry length, so measuring it reads none of
    /// the bytes: a 10 MB upload that expands into hundreds of megabytes of XML is refused for what
    /// it would cost, rather than after it has cost it.</para>
    ///
    /// <para>An upload that is not a readable archive is <em>not</em> refused here: that is
    /// <see cref="XLWorkbook"/>'s own failure to raise, and a corrupt upload must keep failing the
    /// way it already does rather than becoming a "too large" refusal.</para>
    /// </summary>
    private static bool SheetDataExceeds(Stream package, NayaxSalesFileLimits limits)
    {
        if (limits.MaxUncompressedSheetBytes is not long maxBytes)
            return false;

        var start = package.Position;
        try
        {
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            var total = 0L;
            foreach (var entry in archive.Entries)
            {
                if (!IsSheetData(entry.FullName))
                    continue;
                total += entry.Length;
                if (total > maxBytes)
                    return true;
            }

            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            package.Position = start;
        }
    }

    /// <summary>
    /// Whether this package entry is part of the sheet data: a worksheet part, or the shared string
    /// table every text cell of every sheet points into.
    /// </summary>
    private static bool IsSheetData(string entryName) =>
        entryName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
        || entryName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The upload's bytes in memory, for the stream that cannot seek. An uploaded file's stream can,
    /// so this is the fallback that keeps the entry-size check from being silently skipped; what
    /// bounds it is the endpoint's own request size limit.
    /// </summary>
    private static MemoryStream CopyToMemory(Stream content)
    {
        var buffer = new MemoryStream();
        content.CopyTo(buffer);
        buffer.Position = 0;
        return buffer;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        var index = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index += 2;
                    continue;
                }

                quoted = !quoted;
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

            index++;
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

    /// <summary>
    /// A cell's own text, trimmed, or <c>null</c> when the export carries no such column, the cell
    /// is empty, or it holds nothing but whitespace (issue #1298). A cell of spaces states nothing
    /// about a transaction, exactly as an empty one does - the same reading
    /// <see cref="IntInput"/> and <see cref="DecimalInput"/> already apply to the status and the
    /// amount - so it must not reach the import as an empty machine name, payment method or product
    /// name, which the update would then write over a stored one.
    /// </summary>
    private static string? TextValue(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty())
            return null;
        var text = cell.GetString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static long LongValue(IXLCell? cell) => long.TryParse(TextValue(cell), out var value) ? value : 0;

    private static long? NullableLongValue(IXLCell? cell) => long.TryParse(TextValue(cell), out var value) ? value : null;

    private static decimal? DecimalValue(IXLCell? cell) =>
        decimal.TryParse(TextValue(cell), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The transaction-status cell, together with what the export carried there (issue #1298): no
    /// column or a blank cell, a readable status, or a non-blank value that is not one. The third
    /// case used to read as the first, so a status nobody could read looked exactly like a file
    /// that said nothing about the transaction.
    /// </summary>
    private static (int? Value, NayaxSalesCellInput Input) IntInput(IXLCell? cell)
    {
        var text = TextValue(cell);
        if (string.IsNullOrWhiteSpace(text))
            return (null, NayaxSalesCellInput.Absent);
        return int.TryParse(text, out var value)
            ? (value, NayaxSalesCellInput.Valid)
            : (null, NayaxSalesCellInput.Malformed);
    }

    /// <summary>
    /// The settlement-value cell, read the same three ways (issue #1298). An explicit <c>0</c> is a
    /// value the file states; a blank cell or a missing column is not, and is no longer defaulted
    /// to <c>0</c> here. The accepted number formats are unchanged - invariant culture, no currency
    /// symbol - so a value this reader could not read before is now reported as invalid rather than
    /// silently becoming zero.
    /// </summary>
    private static (decimal? Value, NayaxSalesCellInput Input) DecimalInput(IXLCell? cell)
    {
        var text = TextValue(cell);
        if (string.IsNullOrWhiteSpace(text))
            return (null, NayaxSalesCellInput.Absent);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (value, NayaxSalesCellInput.Valid)
            : (null, NayaxSalesCellInput.Malformed);
    }

    /// <summary>
    /// A cell under a column the export itself names as GMT, read as a true UTC instant (issue #380).
    /// The header is what establishes the timezone, so a value carrying no designator is taken as UTC
    /// and one carrying an explicit offset is converted to the same physical instant rather than being
    /// shifted a second time. That reading is <see cref="NayaxGmtTimestamp"/>, the one parser the live
    /// Lynx JSON boundary uses for its own documented-GMT field (issue #471), so neither path can
    /// drift towards reading an offset-free GMT value against the host's time zone.
    ///
    /// The export's own <c>d/M/yyyy h:mm:ss tt</c> text form is tried before that parser, because
    /// <see cref="CultureInfo.InvariantCulture"/> would otherwise read <c>4/10/2026 11:30:00 PM</c> as
    /// 10 April. Offset-aware parsing is deliberately not applied to the machine-local
    /// <c>MachineAuthorizationTime</c> column, where an offset would contradict what the field means;
    /// that column keeps <see cref="DateValue"/>'s rules unchanged.
    /// </summary>
    private static DateTime? UtcDateValue(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty())
            return null;
        if (cell.Value.IsDateTime)
            return DateTime.SpecifyKind(cell.Value.GetDateTime(), DateTimeKind.Utc);
        if (cell.Value.IsNumber)
            return DateTime.SpecifyKind(DateTime.FromOADate(cell.Value.GetNumber()), DateTimeKind.Utc);

        var text = cell.GetString().Trim();
        if (DateTime.TryParseExact(
                text, "d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return DateTime.SpecifyKind(exact, DateTimeKind.Utc);
        return NayaxGmtTimestamp.ParseInstantUtc(text);
    }

    /// <summary>
    /// The <c>AuthorizationDateTimeGMT</c> cell read as a true UTC instant, together with what the
    /// export carried there: no such column, a blank cell, a value <see cref="UtcDateValue"/> cannot
    /// read, or a usable instant (issue #380).
    /// </summary>
    private static (DateTime? Value, NayaxSalesGmtInput Input) GmtValue(IXLCell? cell)
    {
        if (cell is null)
            return (null, NayaxSalesGmtInput.NotProvided);
        if (cell.IsEmpty() || (!cell.Value.IsDateTime && !cell.Value.IsNumber && string.IsNullOrWhiteSpace(cell.GetString())))
            return (null, NayaxSalesGmtInput.Blank);
        var value = UtcDateValue(cell);
        return value is null ? (null, NayaxSalesGmtInput.Malformed) : (value, NayaxSalesGmtInput.Valid);
    }

    private static DateTime? DateValue(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty())
            return null;
        if (cell.Value.IsDateTime)
            return cell.Value.GetDateTime();
        if (cell.Value.IsNumber)
            return DateTime.FromOADate(cell.Value.GetNumber());
        return DateTime.TryParseExact(
            cell.GetString().Trim(), "d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }
}
