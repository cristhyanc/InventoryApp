using System.Text;
using ClosedXML.Excel;

namespace InventoryApi.Services;

internal static class NayaxSalesWorkbook
{
    public static IXLWorkbook Open(Stream stream, bool isCsv)
    {
        if (!isCsv)
            return new XLWorkbook(stream);

        var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Sales");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var rowNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            var values = ParseCsvLine(line);
            for (var column = 0; column < values.Count; column++)
                worksheet.Cell(rowNumber, column + 1).Value = values[column];
            rowNumber++;
        }

        return workbook;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
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
        }

        values.Add(value.ToString());
        return values;
    }
}
