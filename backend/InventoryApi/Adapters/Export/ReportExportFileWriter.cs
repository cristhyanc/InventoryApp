using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Reporting.Export;

namespace InventoryApi.Adapters.Export;

/// <summary>
/// Encodes an already-built <see cref="ReportExportTable"/> (produced by
/// <see cref="GetReportExportRows"/>, the authoritative source of every export cell) as CSV or
/// XLSX bytes. Pure byte-level encoding of already-formatted cells; it must never add, derive, or
/// recompute a report value. Lives in InventoryApi, not Inventory.Application, because Application
/// must not reference ClosedXML.
/// </summary>
public static class ReportExportFileWriter
{
    public static byte[] WriteCsv(ReportExportTable table)
    {
        var builder = new StringBuilder();
        foreach (var row in table.Rows)
            builder.AppendLine(string.Join(",", row.Select(Csv)));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] WriteXlsx(ReportExportTable table)
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
