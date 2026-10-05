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
/// <param name="TransactionStatusId">The raw Nayax transaction-status identifier, classified centrally.</param>
/// <param name="NayaxProductId">The raw remote product identifier, matched against the local catalogue.</param>
/// <param name="MachineName">The machine name as the export reported it.</param>
/// <param name="SettlementValue">The settled amount; <c>0</c> when the file carries none, as the legacy import read it.</param>
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
public sealed record NayaxSalesImportRow(
    long TransactionId,
    long MachineId,
    DateTime? MachineAuthorizationTime,
    int? TransactionStatusId,
    long? NayaxProductId,
    string? MachineName,
    decimal SettlementValue,
    string? PaymentMethod,
    string? ProductName,
    decimal? NayaxProductCostPrice,
    DateTime? AuthorizationDateTimeGmt = null,
    NayaxSalesGmtInput AuthorizationDateTimeGmtInput = NayaxSalesGmtInput.NotProvided);

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
    /// </summary>
    /// <param name="content">The uploaded bytes, positioned at the start.</param>
    /// <param name="fileName">The uploaded file's own name, which decides how the bytes are read.</param>
    IReadOnlyList<NayaxSalesImportRow> Read(Stream content, string fileName);
}
