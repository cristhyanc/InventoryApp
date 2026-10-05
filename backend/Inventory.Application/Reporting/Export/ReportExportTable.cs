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
public sealed record ReportExportTable(IReadOnlyList<IReadOnlyList<string>> Rows, string SheetName);
