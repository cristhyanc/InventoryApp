using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Reporting.Export;

namespace Inventory.Infrastructure.Reporting;

/// <summary>
/// Encodes an already-built <see cref="ReportExportTable"/> (produced by
/// <c>Inventory.Application.Reporting.Export.GetReportExportRows</c>, the authoritative source of
/// every export cell) as CSV or XLSX bytes, behind the Application-owned
/// <see cref="IReportExportFileWriter"/> port. Pure byte-level encoding of already-formatted cells;
/// it must never add, derive, or recompute a report value.
///
/// It lives here, not in <c>Inventory.Application</c>, because Application must not reference
/// ClosedXML, and not in <c>InventoryApi</c> any more because encoding a report file needs no
/// <c>AppDbContext</c> and no HTTP pipeline - the same reason
/// <see cref="Imports.ClosedXmlNayaxSalesWorkbookReader"/> is a real Infrastructure resident
/// (issue #306). It is stateless and holds nothing between calls, so it is registered as a
/// singleton by <c>AddInfrastructureServices()</c>.
/// </summary>
public sealed class ReportExportFileWriter : IReportExportFileWriter
{
    public byte[] WriteCsv(ReportExportTable table)
    {
        var builder = new StringBuilder();
        foreach (var row in table.Rows)
            builder.AppendLine(string.Join(",", row.Select(Csv)));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public byte[] WriteXlsx(ReportExportTable table)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(table.SheetName);
        var rows = table.Rows;
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                sheet.Cell(r + 1, c + 1).Value = rows[r][c];
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
