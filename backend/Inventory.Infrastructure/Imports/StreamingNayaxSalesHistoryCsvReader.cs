using System.Text;
using Inventory.Application.Imports;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// Reads an uploaded Nayax sales history CSV one row at a time, behind the Application's
/// <see cref="INayaxSalesHistoryCsvReader"/> port (issue #1304).
///
/// <para><strong>What makes it a streaming reader.</strong> It holds one line, one list of that
/// line's fields and one <see cref="NayaxSalesImportRow"/> at a time, and yields each row to the
/// caller before reading the next line. Nothing accumulates: there is no worksheet, no list of rows
/// and no second pass, so a 250,000-row history costs the same working set as a 2,000-row one. That
/// is the whole difference from <see cref="ClosedXmlNayaxSalesWorkbookReader"/>, which answers with
/// every row at once and builds a worksheet of cells to read a CSV at all - about a gigabyte for a
/// 114,250-row export, which is the cost issue #1300's 5,000-row ceiling exists to bound.</para>
///
/// <para><strong>Every cell is read exactly as the uploaded import reads it.</strong> The header
/// matching, the quoting rules and all of the value conversions are
/// <see cref="NayaxSalesCells"/>'s - the same type the workbook reader asks for every text cell,
/// which is every cell of a CSV - so the two imports cannot disagree about what a file says. A
/// column this reader does not recognise is ignored, exactly as it is there, and a line with fewer
/// fields than the header row simply states nothing in the columns it does not reach.</para>
///
/// <para><strong>A duplicated column heading is refused, not interpreted.</strong> The workbook
/// reader builds its header map with <c>ToDictionary</c> and so fails outright on a file that names
/// one column twice, and this reader refuses it too - <see cref="Check"/> answers
/// <see cref="NayaxSalesHistoryFileState.DuplicateColumn"/>, so the upload is refused with
/// <c>NayaxSalesHistoryPolicy.DuplicateColumnMessage</c> and nothing is stored. A header row that
/// names a column twice states nothing about which of the two the rows below it belong to, and the
/// one thing the two imports may not do is read the same file differently: a history that quietly
/// took the first occurrence would store different values than the same file uploaded the normal
/// way. The reading is the operator's one-line fix to the header row, not this reader's
/// guess.</para>
///
/// <para><strong>A failure is a failure.</strong> As in the workbook reader, bytes that are not
/// readable text are not reported as an import of zero rows - the exception reaches the caller. What
/// <see cref="Check"/> does report as a refusal is a file whose <em>shape</em> is wrong: no header
/// row, no <c>TransactionID</c> column, no data row, or more rows than the policy allows.</para>
/// </summary>
public sealed class StreamingNayaxSalesHistoryCsvReader : INayaxSalesHistoryCsvReader
{
    /// <summary>
    /// The one column a readable Nayax sales export has to carry, normalised. Every row's identity
    /// starts with its remote transaction identifier (<c>ImportNayaxSales</c>'s first identity
    /// fact), so a file without that column states nothing this import could apply and is refused as
    /// unreadable rather than imported as 250,000 skipped rows.
    /// </summary>
    private static readonly string TransactionIdColumn =
        NayaxSalesCells.NormalizeHeader("TransactionID");

    /// <inheritdoc />
    public NayaxSalesHistoryFileCheck Check(Stream content, int maxDataRows)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = Open(content);

        if (reader.ReadLine() is not { } header)
            return NayaxSalesHistoryFileCheck.Unreadable;

        var columns = Columns(header, out var duplicated);

        // Before the row count, because the refusal is about the header row and counting a quarter
        // of a million lines to say so would be work for nothing.
        if (duplicated)
            return NayaxSalesHistoryFileCheck.DuplicateColumn;

        if (!columns.ContainsKey(TransactionIdColumn))
            return NayaxSalesHistoryFileCheck.Unreadable;

        // The lines are read and discarded: counting 250,000 rows allocates one line at a time, and
        // the first row past the ceiling stops the count - so a file ten times the limit costs no
        // more to refuse than one row over it.
        var rows = 0;
        while (reader.ReadLine() is not null)
        {
            rows++;
            if (rows > maxDataRows)
                return NayaxSalesHistoryFileCheck.TooManyRows;
        }

        return rows == 0 ? NayaxSalesHistoryFileCheck.NoRows : NayaxSalesHistoryFileCheck.Readable(rows);
    }

    /// <inheritdoc />
    public IEnumerable<NayaxSalesImportRow> ReadRows(Stream content, int skipDataRows)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(skipDataRows);

        return Rows(content, skipDataRows);
    }

    /// <summary>
    /// The iterator itself. Separate from <see cref="ReadRows"/> so the argument checks above run
    /// when the method is called rather than when the sequence is first enumerated.
    /// </summary>
    private static IEnumerable<NayaxSalesImportRow> Rows(Stream content, int skipDataRows)
    {
        using var reader = Open(content);

        if (reader.ReadLine() is not { } header)
            yield break;

        var columns = Columns(header, out var duplicated);

        // Unreachable through the upload, which refuses such a file before a job exists for it
        // (Check above). Stated here as well so that the two ways into this reader cannot answer
        // differently about one header row, and so that a caller that skipped the check fails
        // loudly rather than importing a column it chose - which is what the workbook reader does
        // with the same file.
        if (duplicated)
        {
            throw new InvalidOperationException(
                "The sales history's header row names the same column twice, so which column the "
                + "rows belong to is not stated. It is refused at upload.");
        }

        var gmtColumn = Index(columns, "AuthorizationDateTimeGMT") is not null;

        // The header row is row 1, which is what makes a reported row number one an operator can
        // find in the file - the same numbering the workbook reader's rows carry.
        var rowNumber = 1;
        var dataRows = 0;
        while (reader.ReadLine() is { } line)
        {
            rowNumber++;
            dataRows++;

            // The rows a previous attempt of this job already committed. The walk over the same
            // bytes is deterministic, so the n-th data row is the same row on every attempt; the
            // lines are still read, because a CSV has no index, but nothing of them is converted.
            if (dataRows <= skipDataRows)
                continue;

            yield return Row(NayaxSalesCells.ParseLine(line), columns, gmtColumn, rowNumber);
        }
    }

    /// <summary>
    /// One data row's raw facts, read by exactly the conversions
    /// <see cref="ClosedXmlNayaxSalesWorkbookReader"/> applies to the same columns.
    /// </summary>
    private static NayaxSalesImportRow Row(
        IReadOnlyList<string> values,
        IReadOnlyDictionary<string, int> columns,
        bool gmtColumn,
        int rowNumber)
    {
        var (gmt, gmtInput) = Gmt(Value(values, columns, "AuthorizationDateTimeGMT"), gmtColumn);
        var (status, statusInput) = NayaxSalesCells.IntInput(Value(values, columns, "TransactionStatusId"));
        var (settlement, settlementInput) =
            NayaxSalesCells.DecimalInput(Value(values, columns, "SettlementValue"));

        return new NayaxSalesImportRow(
            NayaxSalesCells.Long(Value(values, columns, "TransactionID")),
            NayaxSalesCells.Long(Value(values, columns, "MachineID")),
            NayaxSalesCells.ExportDate(Value(values, columns, "MachineAuthorizationTime")),
            status,
            NayaxSalesCells.NullableLong(Value(values, columns, "NayaxProductId")),
            NayaxSalesCells.Text(Value(values, columns, "MachineName")),
            settlement,
            NayaxSalesCells.Text(Value(values, columns, "PaymentMethod")),
            NayaxSalesCells.Text(Value(values, columns, "ProductName")),
            NayaxSalesCells.Decimal(Value(values, columns, "ProductCostPrice", "ProductCost", "CostPrice")),
            gmt,
            gmtInput,
            statusInput,
            settlementInput,
            rowNumber);
    }

    /// <summary>
    /// What the export carried in its <c>AuthorizationDateTimeGMT</c> column (issue #380): no such
    /// column at all, a blank cell, a value that is not a readable instant, or a usable one. The
    /// first two are deliberately distinguished even though the import's instant precedence treats
    /// them alike, because what the file carried is the fact this row records.
    /// </summary>
    private static (DateTime? Value, NayaxSalesGmtInput Input) Gmt(string? raw, bool columnPresent)
    {
        if (!columnPresent)
            return (null, NayaxSalesGmtInput.NotProvided);
        if (NayaxSalesCells.Text(raw) is null)
            return (null, NayaxSalesGmtInput.Blank);

        var value = NayaxSalesCells.UtcDate(raw);
        return value is null ? (null, NayaxSalesGmtInput.Malformed) : (value, NayaxSalesGmtInput.Valid);
    }

    /// <summary>
    /// The header row's columns, normalised to their letters and digits and mapped to their
    /// position. An empty heading names no column.
    /// </summary>
    /// <param name="header">The file's first line.</param>
    /// <param name="duplicated">
    /// Whether the row names one column twice, which is a refusal rather than a reading: both
    /// callers above say so instead of choosing an occurrence.
    /// </param>
    private static IReadOnlyDictionary<string, int> Columns(string header, out bool duplicated)
    {
        var fields = NayaxSalesCells.ParseLine(header);
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        duplicated = false;
        for (var index = 0; index < fields.Count; index++)
            if (NayaxSalesCells.Text(fields[index]) is { } name
                && !columns.TryAdd(NayaxSalesCells.NormalizeHeader(name), index))
            {
                duplicated = true;
            }

        return columns;
    }

    /// <summary>
    /// This row's value under the first of <paramref name="names"/> the header row carries, or
    /// <see langword="null"/> when it carries none of them or the line does not reach that column.
    /// </summary>
    private static string? Value(
        IReadOnlyList<string> values, IReadOnlyDictionary<string, int> columns, params string[] names)
    {
        if (Index(columns, names) is not { } index)
            return null;

        return index < values.Count ? values[index] : null;
    }

    /// <summary>The position of the first of <paramref name="names"/> the header row carries.</summary>
    private static int? Index(IReadOnlyDictionary<string, int> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(NayaxSalesCells.NormalizeHeader(name), out var index))
                return index;

        return null;
    }

    /// <summary>
    /// Reads the uploaded bytes as text, with the same encoding handling the workbook reader's CSV
    /// path uses: UTF-8 unless a byte-order mark says otherwise, and the stream left to its owner.
    /// </summary>
    private static StreamReader Open(Stream content) =>
        new(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
}
