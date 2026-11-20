namespace Inventory.Application.Imports;

/// <summary>
/// What checking an uploaded sales history file found (issue #1304). Exactly one of the five, and
/// four of them are refusals the operator is told about with nothing stored.
/// </summary>
public enum NayaxSalesHistoryFileState
{
    /// <summary>A readable Nayax sales export, inside the row ceiling, with at least one data row.</summary>
    Readable,

    /// <summary>
    /// Not a readable Nayax sales export: no header row at all, or a header row that does not carry
    /// the one column every row's identity depends on.
    /// </summary>
    Unreadable,

    /// <summary>A readable export whose header row is followed by no data row.</summary>
    NoRows,

    /// <summary>
    /// More data rows than <c>Inventory.Domain.Imports.NayaxSalesHistoryPolicy.MaxRows</c>. The
    /// reader stops counting at the first row past the ceiling, so a file ten times the limit costs
    /// no more to refuse than one row over it.
    /// </summary>
    TooManyRows,

    /// <summary>
    /// A header row that names the same column twice, which states nothing about which of the two
    /// the rows below it are read from. It is refused rather than interpreted: the uploaded import's
    /// reader fails outright on such a file, and a history that silently read one of the columns
    /// would apply different values to the same business's sales than the same file uploaded the
    /// other way.
    /// </summary>
    DuplicateColumn,
}

/// <summary>
/// What one check of an uploaded history answered (issue #1304).
/// </summary>
/// <param name="State">What the file is.</param>
/// <param name="DataRows">
/// How many data rows it carries, excluding the header row. <c>0</c> for every state but
/// <see cref="NayaxSalesHistoryFileState.Readable"/>, because a refused file is not counted.
/// </param>
public sealed record NayaxSalesHistoryFileCheck(NayaxSalesHistoryFileState State, int DataRows)
{
    /// <summary>A readable export of <paramref name="dataRows"/> rows.</summary>
    public static NayaxSalesHistoryFileCheck Readable(int dataRows) =>
        new(NayaxSalesHistoryFileState.Readable, dataRows);

    /// <summary>A file that is not a readable Nayax sales export.</summary>
    public static NayaxSalesHistoryFileCheck Unreadable { get; } =
        new(NayaxSalesHistoryFileState.Unreadable, 0);

    /// <summary>A readable export with nothing in it to import.</summary>
    public static NayaxSalesHistoryFileCheck NoRows { get; } =
        new(NayaxSalesHistoryFileState.NoRows, 0);

    /// <summary>A file carrying more rows than the policy allows.</summary>
    public static NayaxSalesHistoryFileCheck TooManyRows { get; } =
        new(NayaxSalesHistoryFileState.TooManyRows, 0);

    /// <summary>A file whose header row names one column twice.</summary>
    public static NayaxSalesHistoryFileCheck DuplicateColumn { get; } =
        new(NayaxSalesHistoryFileState.DuplicateColumn, 0);

    /// <summary>Whether the file may be stored as a job.</summary>
    public bool IsReadable => State == NayaxSalesHistoryFileState.Readable;
}

/// <summary>
/// Reads an uploaded Nayax sales history CSV <strong>row by row</strong> (issue #1304), behind which
/// the CSV format lives entirely - the quoting rules, the header spellings this export has been seen
/// to use and the cell-to-value conversions are all the adapter's business, exactly as they are for
/// <see cref="INayaxSalesWorkbookReader"/>. The use case never sees a line, a column or a header
/// name.
///
/// <para><strong>Why a second reader exists at all.</strong> <see cref="INayaxSalesWorkbookReader"/>
/// answers with every row of the file at once, and for a CSV it builds a worksheet of cells to do it:
/// a 114,250-row export allocated about a gigabyte that way, which is what issue #1300's 5,000-row
/// ceiling exists to bound. A history of 250,000 rows cannot be read that way at all, so this port
/// streams - the implementation yields one row at a time and holds no row it has yielded, so what the
/// read costs stops growing with the length of the history. The <em>rows</em> it yields are the same
/// <see cref="NayaxSalesImportRow"/> facts, read by the same conversions, so the import rules applied
/// to them are identical.</para>
///
/// <para><strong>CSV only, on purpose.</strong> A compressed workbook cannot be read row by row
/// without expanding it, which is the cost this whole import exists to avoid, so a history is a CSV
/// and <c>NayaxSalesHistoryPolicy.UnsupportedFormatMessage</c> says so.</para>
/// </summary>
public interface INayaxSalesHistoryCsvReader
{
    /// <summary>
    /// Reads <paramref name="content"/> once to decide whether it may be stored as a job, and counts
    /// its data rows. It allocates nothing per row: the lines are read and discarded.
    /// </summary>
    /// <param name="content">The uploaded bytes, positioned at the start.</param>
    /// <param name="maxDataRows">
    /// The row ceiling, from <c>Inventory.Domain.Imports.NayaxSalesHistoryPolicy.MaxRows</c>. The
    /// reader stops at the first row past it and answers
    /// <see cref="NayaxSalesHistoryFileState.TooManyRows"/>.
    /// </param>
    NayaxSalesHistoryFileCheck Check(Stream content, int maxDataRows);

    /// <summary>
    /// The file's data rows, in file order, one at a time.
    ///
    /// <para>It is lazy: nothing is read until the sequence is enumerated, and a row the caller has
    /// moved past is not held. A caller that stops enumerating stops the reading.</para>
    /// </summary>
    /// <param name="content">The uploaded bytes, positioned at the start.</param>
    /// <param name="skipDataRows">
    /// How many data rows at the start of the file to pass over without yielding them - the rows a
    /// previous attempt of the same job already committed. The walk over the same bytes is
    /// deterministic, so the n-th data row is the same row on every attempt.
    /// </param>
    IEnumerable<NayaxSalesImportRow> ReadRows(Stream content, int skipDataRows);
}
