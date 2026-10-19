namespace Inventory.Application.Reporting.Export;

/// <summary>
/// A report export result as rows of already-formatted string cells (a header row, then one row
/// per record, with an optional trailing totals row), plus the sheet name an XLSX writer should
/// use. Building these rows only formats values already produced by the authoritative report use
/// cases; it must never recompute or duplicate a financial formula. Byte-level CSV/XLSX encoding is
/// an adapter concern behind the <see cref="IReportExportFileWriter"/> port (implemented by
/// Inventory.Infrastructure.Reporting.ReportExportFileWriter since issue #306), since ClosedXML
/// cannot be referenced from Application.
/// </summary>
public sealed record ReportExportTable(IReadOnlyList<IReadOnlyList<string>> Rows, string SheetName)
{
    /// <summary>
    /// The first cell of the one trailing metadata row a report with no data rows at all carries,
    /// so an empty download still states the availability behind its emptiness (issue #731).
    ///
    /// <para>The availability state columns of the per-record reports - transaction sales, product
    /// profitability and machine profitability - live on data rows, because a downloaded row is read
    /// on its own. A result with no rows would therefore have carried column headings and no state,
    /// leaving a <c>Missing</c> or <c>Unverified</c> period indistinguishable from a
    /// <c>Complete</c> period that genuinely sold nothing - the exact confusion the availability
    /// model exists to prevent. Rather than fabricate a transaction, product or machine row to carry
    /// the state, those reports append one row of this shape: this label in the first column, the
    /// report's own availability cells in the availability columns, and every other cell empty.
    /// It is the same marked-row convention the daily and reconciliation exports already use for
    /// their <c>TOTAL</c> row (those two always carry a totals row, so they are never stateless),
    /// and because both formats encode this one table, the CSV and the XLSX say it identically.
    /// A report that has data rows never carries it.</para>
    /// </summary>
    public const string NoRowsLabel = "NO ROWS";
}
