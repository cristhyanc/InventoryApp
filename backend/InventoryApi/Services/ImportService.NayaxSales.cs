using ClosedXML.Excel;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace InventoryApi.Services;

public sealed partial class ImportService
{
    public async Task<NayaxSalesImportResult> ImportNayaxSalesFromExcelAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
            throw new InvalidOperationException("An Excel file is required.");
        if (!new[] { ".xlsx", ".xls", ".csv" }.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .xlsx, .xls, or .csv files are supported.");

        using var stream = file.OpenReadStream();
        using var workbook = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? CreateCsvWorkbook(stream)
            : new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet is null) return new(0, 0, 0);
        var rows = worksheet.Rows().ToList();
        if (rows.Count == 0) return new(0, 0, 0);

        var headers = rows[0].Cells()
            .Where(cell => !cell.IsEmpty())
            .ToDictionary(cell => NormalizeHeader(cell.GetString()), cell => cell.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var row in rows.Skip(1))
        {
            var transactionId = LongValue(Cell(row, headers, "TransactionID"));
            var machineId = LongValue(Cell(row, headers, "MachineID"));
            var authorizationTime = DateValue(Cell(row, headers, "MachineAuthorizationTime"));
            if (transactionId <= 0 || machineId <= 0 || authorizationTime is null)
            {
                skipped++;
                continue;
            }

            var sale = new NayaxSales
            {
                TransactionID = transactionId, MachineID = machineId, MachineAuthorizationTime = authorizationTime.Value,
                TransactionStatusId = IntValue(Cell(row, headers, "TransactionStatusId")),
                NayaxProductId = NullableLongValue(Cell(row, headers, "NayaxProductId")),
                MachineName = TextValue(Cell(row, headers, "MachineName")),
                SettlementValue = DecimalValue(Cell(row, headers, "SettlementValue")) ?? 0m,
                PaymentMethod = TextValue(Cell(row, headers, "PaymentMethod")),
                ProductName = TextValue(Cell(row, headers, "ProductName"))
            };
            var existing = await _db.NayaxSales.FirstOrDefaultAsync(x => x.TransactionID == transactionId, cancellationToken);
            if (existing is null)
            {
                _db.NayaxSales.Add(sale);
                await _saleCosting.CostSaleAsync(sale, allowLegacyEstimate: true, cancellationToken: cancellationToken);
                imported++;
            }
            else
            {
                existing.MachineID = sale.MachineID; existing.TransactionStatusId = sale.TransactionStatusId;
                existing.NayaxProductId = sale.NayaxProductId; existing.MachineName = sale.MachineName;
                existing.SettlementValue = sale.SettlementValue; existing.PaymentMethod = sale.PaymentMethod;
                existing.ProductName = sale.ProductName; existing.MachineAuthorizationTime = sale.MachineAuthorizationTime;
                await _saleCosting.CostSaleAsync(existing, allowLegacyEstimate: true, cancellationToken: cancellationToken);
                updated++;
            }
        }
        if (imported > 0 || updated > 0) await _db.SaveChangesAsync(cancellationToken);
        return new(imported, updated, skipped);
    }

    private static IXLWorkbook CreateCsvWorkbook(Stream stream)
    {
        var path = Path.GetTempFileName();
        using var destination = File.Create(path);
        stream.CopyTo(destination);
        return new XLWorkbook(path);
    }
    private static string NormalizeHeader(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    private static IXLCell? Cell(IXLRow row, IReadOnlyDictionary<string, int> headers, string name) =>
        headers.TryGetValue(NormalizeHeader(name), out var index) ? row.Cell(index) : null;
    private static string? TextValue(IXLCell? cell) => cell is null || cell.IsEmpty() ? null : cell.GetString().Trim();
    private static long LongValue(IXLCell? cell) => long.TryParse(TextValue(cell), out var value) ? value : 0;
    private static long? NullableLongValue(IXLCell? cell) => long.TryParse(TextValue(cell), out var value) ? value : null;
    private static int? IntValue(IXLCell? cell) => int.TryParse(TextValue(cell), out var value) ? value : null;
    private static decimal? DecimalValue(IXLCell? cell) => decimal.TryParse(TextValue(cell), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static DateTime? DateValue(IXLCell? cell) =>
        cell is null || cell.IsEmpty() ? null :
        cell.Value.IsDateTime ? cell.Value.GetDateTime() :
        cell.Value.IsNumber ? DateTime.FromOADate(cell.Value.GetNumber()) :
        DateTime.TryParseExact(cell.GetString().Trim(), "d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
}
