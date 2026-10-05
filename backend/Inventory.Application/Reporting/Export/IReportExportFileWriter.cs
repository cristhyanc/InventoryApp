namespace Inventory.Application.Reporting.Export;

/// <summary>
/// Narrow port for encoding an already-built <see cref="ReportExportTable"/> (produced by
/// <see cref="GetReportExportRows"/>, the authoritative source of every export cell) as CSV or
/// XLSX bytes.
///
/// It exists so that the spreadsheet library stays an Infrastructure detail: Application owns the
/// contract, <c>Inventory.Infrastructure.Reporting.ReportExportFileWriter</c> owns ClosedXML, and
/// <c>ReportsController</c> depends on neither (issue #306). An implementation performs pure
/// byte-level encoding of already-formatted cells; it must never add, derive, or recompute a report
/// value, because a financial figure that an exporter could change would no longer match the API
/// response it was exported from.
/// </summary>
public interface IReportExportFileWriter
{
    /// <summary>Encodes <paramref name="table"/> as UTF-8 CSV bytes.</summary>
    byte[] WriteCsv(ReportExportTable table);

    /// <summary>Encodes <paramref name="table"/> as a single-worksheet XLSX workbook.</summary>
    byte[] WriteXlsx(ReportExportTable table);
}
