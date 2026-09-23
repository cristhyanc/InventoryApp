using ClosedXML.Excel;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
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
        using var workbook = NayaxSalesWorkbook.Open(
            stream, file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
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
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var affected = new Dictionary<long, DateTime>();

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
                TransactionID = transactionId,
                MachineID = machineId,
                MachineAuthorizationTime = authorizationTime.Value,
                TransactionStatusId = IntValue(Cell(row, headers, "TransactionStatusId")),
                NayaxProductId = NullableLongValue(Cell(row, headers, "NayaxProductId")),
                MachineName = TextValue(Cell(row, headers, "MachineName")),
                SettlementValue = DecimalValue(Cell(row, headers, "SettlementValue")) ?? 0m,
                PaymentMethod = TextValue(Cell(row, headers, "PaymentMethod")),
                ProductName = TextValue(Cell(row, headers, "ProductName")),
                NayaxProductCostPrice = DecimalValue(Cell(row, headers,
                    "ProductCostPrice", "ProductCost", "CostPrice"))
            };
            var existing = await _db.NayaxSales.FirstOrDefaultAsync(x => x.TransactionID == transactionId, cancellationToken);
            if (existing is null)
            {
                _db.NayaxSales.Add(sale);
                await _saleCosting.CostSaleAsync(sale, cancellationToken: cancellationToken);
                imported++;
            }
            else
            {
                TrackAffectedProduct(products, existing, affected);
                existing.MachineID = sale.MachineID;
                existing.TransactionStatusId = sale.TransactionStatusId;
                existing.NayaxProductId = sale.NayaxProductId;
                existing.MachineName = sale.MachineName;
                existing.SettlementValue = sale.SettlementValue;
                existing.PaymentMethod = sale.PaymentMethod;
                existing.ProductName = sale.ProductName;
                existing.MachineAuthorizationTime = sale.MachineAuthorizationTime;

                if (sale.NayaxProductCostPrice.HasValue)
                    existing.NayaxProductCostPrice = sale.NayaxProductCostPrice;
                await _saleCosting.CostSaleAsync(existing, cancellationToken: cancellationToken);
                updated++;
            }
            TrackAffectedProduct(products, existing ?? sale, affected);
        }
        if (imported > 0 || updated > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await RebuildPostTransitionProductsAsync(affected, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return new(imported, updated, skipped);
    }

    private static void TrackAffectedProduct(
        IReadOnlyCollection<Product> products,
        NayaxSales sale,
        IDictionary<long, DateTime> affected)
    {
        if (!NayaxTransactionStatusClassifier.IsCompletedSale(sale))
            return;
        var product = NayaxProductMatcher.Match(products, sale.NayaxProductId, sale.ProductName);
        if (product is not null &&
            (!affected.TryGetValue(product.Id, out var existing) || sale.MachineAuthorizationTime < existing))
            affected[product.Id] = sale.MachineAuthorizationTime;
    }

    private async Task RebuildPostTransitionProductsAsync(
        IReadOnlyDictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        if (affected.Count == 0)
            return;
        var baselines = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => affected.Keys.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);
        foreach (var item in affected)
            if (baselines.TryGetValue(item.Key, out var cutoff) && item.Value > cutoff)
                await _inventoryCostRebuild.RebuildAsync(item.Key, item.Value, cancellationToken: cancellationToken);
    }

    private static string NormalizeHeader(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    private static IXLCell? Cell(IXLRow row, IReadOnlyDictionary<string, int> headers, params string[] names)
    {
        foreach (var name in names)
            if (headers.TryGetValue(NormalizeHeader(name), out var index))
                return row.Cell(index);
        return null;
    }
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
