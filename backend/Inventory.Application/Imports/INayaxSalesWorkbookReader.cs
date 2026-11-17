namespace Inventory.Application.Imports;

/// <summary>
/// One uploaded Nayax transaction export, as the import reads it (issue #301). It carries the
/// uploaded name and a way to open the bytes, never an <c>IFormFile</c> or a filesystem path, so
/// the transport type stays at the HTTP boundary - the same shape
/// <c>Purchases.PurchaseFileInput</c> and <c>Expenses.ExpenseAttachmentInput</c> already use.
/// </summary>
/// <param name="FileName">The uploaded file's own name; it decides the accepted format.</param>
/// <param name="OpenReadStream">Opens the uploaded bytes for reading. The import disposes the stream.</param>
public sealed record NayaxSalesFileInput(string FileName, Func<Stream> OpenReadStream);

/// <summary>
/// One raw data row of an uploaded Nayax transaction export, exactly as the workbook/CSV reader
/// found it (issue #301). Nothing here is validated or defaulted by a business rule: a value the
/// file does not carry, or carries unparsably, is <c>null</c> (<c>0</c> for the two identifiers,
/// which the file always has to carry), and <see cref="ImportNayaxSales"/> decides which rows that
/// makes importable. Keeping the raw facts separate from the accounting values derived from them is
/// the Nayax integration rule in AGENTS.md.
///
/// <para>Since issue #1298 the status and the settlement value additionally say <em>what the file
/// carried</em>, because "the file states nothing" and "the file states something unreadable" are
/// different facts about a stored sale and used to look identical here. The value property is
/// non-<c>null</c> exactly when its companion input is
/// <see cref="NayaxSalesCellInput.Valid"/>, so a present value is what the file actually
/// stated - an explicit <c>0</c> amount included.</para>
/// </summary>
/// <param name="TransactionId">The remote Nayax transaction identifier; <c>0</c> when absent or unparsable.</param>
/// <param name="MachineId">The remote Nayax machine identifier; <c>0</c> when absent or unparsable.</param>
/// <param name="MachineAuthorizationTime">
/// The export's own <c>MachineAuthorizationTime</c> column, or <c>null</c> when absent or unparsable.
/// In the Nayax Lynx API this field is machine-local wall-clock time (issue #380); the export file's
/// own column semantics are undocumented, so this value's timezone is unverified and it is used as a
/// sale instant only for a new sale whose export carries no usable GMT value (see
/// <see cref="ImportNayaxSales"/>).
/// </param>
/// <param name="TransactionStatusId">
/// The raw Nayax transaction-status identifier, classified centrally, or <c>null</c> when the file
/// states none - a blank cell, a missing column, or a cell this reader could not read. Which of
/// those it was is <paramref name="TransactionStatusInput"/>.
/// </param>
/// <param name="NayaxProductId">The raw remote product identifier, matched against the local catalogue.</param>
/// <param name="MachineName">
/// The machine name as the export reported it, or <c>null</c> when the file states none - a missing
/// column, an empty cell, or a cell holding nothing but whitespace (issue #1298). The same holds for
/// <paramref name="PaymentMethod"/> and <paramref name="ProductName"/>: a value the file does not
/// state must not reach <see cref="ImportNayaxSales"/> as an empty string, which it could not tell
/// from a reported blank and would write over a stored value.
/// </param>
/// <param name="SettlementValue">
/// The settled amount the file states, or <c>null</c> when it states none (issue #1298). It is no
/// longer defaulted to <c>0</c>: a blank cell and a missing column used to be indistinguishable
/// from an explicit zero, which is how an export without the column wiped a stored sale's amount.
/// </param>
/// <param name="PaymentMethod">The raw payment method, classified centrally wherever it is read.</param>
/// <param name="ProductName">The product name as the export reported it; the name half of product matching.</param>
/// <param name="NayaxProductCostPrice">The raw transaction-level Nayax <c>Product Cost Price</c>.</param>
/// <param name="AuthorizationDateTimeGmt">
/// The export's <c>AuthorizationDateTimeGMT</c> column normalized to a true UTC instant, or
/// <c>null</c> when the row carries no usable value (issue #380). This is the authoritative sale
/// instant wherever it is present, matching the Lynx API field of the same name.
/// </param>
/// <param name="AuthorizationDateTimeGmtInput">
/// What the export actually carried in that column - no column, a blank cell, an unparsable value or
/// a usable instant - so the import can tell a legacy export without the column from an authoritative
/// value it failed to read, and never treats the latter as permission to fall back.
/// </param>
/// <param name="TransactionStatusInput">
/// What the export carried in the status cell (issue #1298): nothing, a readable status, or a
/// non-blank value that is not one.
/// </param>
/// <param name="SettlementValueInput">What the export carried in the settlement-value cell (issue #1298).</param>
/// <param name="RowNumber">
/// The row's own number in the file, counting the header row as row 1, so an invalid row can be
/// reported where an operator can find it (issue #1298). <c>0</c> for a row no file produced.
/// </param>
public sealed record NayaxSalesImportRow(
    long TransactionId,
    long MachineId,
    DateTime? MachineAuthorizationTime,
    int? TransactionStatusId,
    long? NayaxProductId,
    string? MachineName,
    decimal? SettlementValue,
    string? PaymentMethod,
    string? ProductName,
    decimal? NayaxProductCostPrice,
    DateTime? AuthorizationDateTimeGmt = null,
    NayaxSalesGmtInput AuthorizationDateTimeGmtInput = NayaxSalesGmtInput.NotProvided,
    NayaxSalesCellInput TransactionStatusInput = NayaxSalesCellInput.Absent,
    NayaxSalesCellInput SettlementValueInput = NayaxSalesCellInput.Absent,
    int RowNumber = 0);

/// <summary>
/// What an uploaded export row carried in one cell whose absence must not overwrite a stored sale
/// (issue #1298). Three cases, because the first two used to be one:
/// </summary>
public enum NayaxSalesCellInput
{
    /// <summary>The export has no such column, or this row's cell is blank. The file states nothing.</summary>
    Absent,

    /// <summary>The cell holds a readable value, carried in the row's own value property. An explicit <c>0</c> is one.</summary>
    Valid,

    /// <summary>The cell holds a non-blank value this reader cannot read. The row is invalid, never blank.</summary>
    Malformed,
}

/// <summary>
/// What an uploaded export row carried in its <c>AuthorizationDateTimeGMT</c> column (issue #380).
/// </summary>
public enum NayaxSalesGmtInput
{
    /// <summary>The export has no <c>AuthorizationDateTimeGMT</c> column at all.</summary>
    NotProvided,

    /// <summary>The column exists but this row's cell is empty.</summary>
    Blank,

    /// <summary>The cell holds a value that is not a readable instant.</summary>
    Malformed,

    /// <summary>The cell holds a readable instant, carried in <c>AuthorizationDateTimeGmt</c>.</summary>
    Valid,
}

/// <summary>
/// The upload formats <see cref="INayaxSalesWorkbookReader"/> can read, stated once because more
/// than one use case now accepts a Nayax transaction export: <see cref="ImportNayaxSales"/> imports
/// one, and the sale timestamp repair preview reads one for its authorization instants alone
/// (issue #472).
/// </summary>
public static class NayaxSalesExportFormats
{
    /// <summary>The accepted file extensions, compared case-insensitively.</summary>
    public static readonly string[] Supported = [".xlsx", ".xls", ".csv"];

    /// <summary>The caller-facing refusal, unchanged from the import's long-standing message.</summary>
    public const string UnsupportedMessage = "Only .xlsx, .xls, or .csv files are supported.";

    public static bool IsSupported(string fileName) =>
        Supported.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// How much of an upload a caller is willing to read (issue #1300). The numbers themselves are
/// <c>Inventory.Domain.Imports.NayaxSalesFilePolicy</c>'s; this record is how a caller states them
/// to the reader, because the limits have to be enforced <em>while</em> the bytes are being read -
/// the memory a file costs is spent by then, so a use case that checked afterwards would already
/// have paid it.
///
/// <para><see cref="None"/> is what a caller that imports nothing passes: the sale timestamp repair
/// preview (issue #472) reads an export for its authorization instants alone, and capping it is a
/// decision of its own rather than a side effect of this one.</para>
/// </summary>
/// <param name="MaxDataRows">
/// How many data rows may be read, excluding the header row, or <c>null</c> for no limit. The reader
/// stops as soon as it finds one more than this, so the rows past the limit are never converted.
/// </param>
/// <param name="MaxUncompressedSheetBytes">
/// How far a compressed workbook's sheet data may expand, or <c>null</c> for no limit. It is checked
/// from the package's own entry sizes before the workbook is loaded, which is the only point at
/// which an XLSX's expansion can still be refused rather than paid for.
/// </param>
public sealed record NayaxSalesFileLimits(int? MaxDataRows, long? MaxUncompressedSheetBytes)
{
    /// <summary>No limit at all: read whatever the file holds.</summary>
    public static NayaxSalesFileLimits None { get; } = new(null, null);
}

/// <summary>
/// What reading an upload produced (issue #1300): its rows, or the fact that it is beyond the
/// limits the caller stated and was therefore not read.
///
/// <para>The two are deliberately one answer rather than an exception from the adapter: "this file
/// is too large to import" is the use case's rule and the operator's refusal, and the reader's job
/// is to say what it found - as it already does for a file carrying no data row at all.</para>
/// </summary>
/// <param name="Rows">The data rows, in file order; empty when the file carries none, and empty when it was refused.</param>
/// <param name="ExceedsLimits">
/// <see langword="true"/> when the file carries more data rows than
/// <see cref="NayaxSalesFileLimits.MaxDataRows"/>, or sheet data expanding past
/// <see cref="NayaxSalesFileLimits.MaxUncompressedSheetBytes"/>. Nothing was parsed, so
/// <paramref name="Rows"/> is empty and says nothing about the file.
/// </param>
public sealed record NayaxSalesWorkbookReadResult(
    IReadOnlyList<NayaxSalesImportRow> Rows, bool ExceedsLimits)
{
    /// <summary>A file that carries no data row at all.</summary>
    public static NayaxSalesWorkbookReadResult Empty { get; } = new([], false);

    /// <summary>A file beyond the caller's limits: nothing was read.</summary>
    public static NayaxSalesWorkbookReadResult BeyondLimits { get; } = new([], true);
}

/// <summary>
/// Reads an uploaded Nayax transaction export into raw rows (issue #301), behind which the
/// workbook format lives entirely: ClosedXML, the CSV quoting rules, the header spellings this
/// export has been seen to use and the cell-to-value conversions are all the adapter's business,
/// implemented by <c>Inventory.Infrastructure.Imports.ClosedXmlNayaxSalesWorkbookReader</c>. The
/// use case never sees a worksheet, a cell or a header name.
/// </summary>
public interface INayaxSalesWorkbookReader
{
    /// <summary>
    /// Reads every data row of <paramref name="content"/>, in file order, and answers an empty
    /// list when the file carries no data rows at all (no worksheet, no rows, or a header row
    /// only). A file whose bytes are not a readable workbook of the format
    /// <paramref name="fileName"/> claims is a failure and is not reported as an empty import.
    ///
    /// <para>A file beyond <paramref name="limits"/> is answered as
    /// <see cref="NayaxSalesWorkbookReadResult.BeyondLimits"/> and is <em>not</em> read: that is the
    /// whole point of passing the limits in rather than counting afterwards (issue #1300).</para>
    /// </summary>
    /// <param name="content">The uploaded bytes, positioned at the start.</param>
    /// <param name="fileName">The uploaded file's own name, which decides how the bytes are read.</param>
    /// <param name="limits">How much of the file this caller is willing to read.</param>
    NayaxSalesWorkbookReadResult Read(Stream content, string fileName, NayaxSalesFileLimits limits);
}
