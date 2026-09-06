using ClosedXML.Excel;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Reflection;

namespace InventoryApi.Services;

public class MachineService : IMachineService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly ISaleCostingService _saleCosting;
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;

    public MachineService(AppDbContext db, INayaxLynxClient nayaxLynxClient, ISaleCostingService? saleCosting = null, INayaxProcessingFeeService? nayaxProcessingFees = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _saleCosting = saleCosting ?? new SaleCostingService(db);
        _nayaxProcessingFees = nayaxProcessingFees ?? new NayaxProcessingFeeService(db);
    }

    public async Task<Machine?> GetById(long id)
    {
        var nayaxMachine = await _nayaxLynxClient.GetMachineAsync(id);
        if (nayaxMachine == null) return null;
        var products = await _db.Products.ToListAsync();
        return await GetMachineSalesAsync(nayaxMachine, products);
    }

    public async Task<List<Machine>> GetAll()
    {
        var nayaxMachines = await _nayaxLynxClient.GetMachinesAsync();
        await SaveMachinesLastSalesAsync(nayaxMachines.Select(x => x.MachineID).ToList());
        var products = await _db.Products.ToListAsync();
        var machines = new List<Machine>();
        foreach (var machine in nayaxMachines)
            machines.Add(await GetMachineSalesAsync(machine, products));
        return machines;
    }

    public async Task<List<Product>> GetMachineProducts(long id)
    {
        var nayaxMachineProducts = await _nayaxLynxClient.GetMachineProductsAsync(id);
        var productList = await _db.Products.Include(x => x.Category).AsNoTracking().ToListAsync();
        var products = nayaxMachineProducts.Select(mp =>
        {
            var result = productList.First(p => p.Id == mp.NayaxProductID).Clone();
            result.MachinePrice = mp.RetailPrice ?? 0;
            result.CommissionValue = mp.CommissionValue ?? 0;
            result.SuggestedNetValue = result.MachinePrice - (result.MachinePrice * result.CommissionValue / 100) - result.UnitPrice - (decimal)0.18;
            result.SuggestedPriceValue = ((decimal)0.18 + result.UnitPrice) / ((decimal)0.5 - (result.CommissionValue / 100));
            result.MdbCode = mp.MDBCode;
            result.QuantityInStock = (mp.PAR - mp.MissingStockByMDB) ?? 0;
            result.MaxStockInMachine = mp.PAR;
            return result;
        }).ToList().OrderBy(x => x.MdbCode).ToList();

        return products;
    }

    public async Task<(int Imported, int Updated, int Skipped)> ImportNayaxSalesFromExcelAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return (0, 0, 0);

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !file.FileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) &&
            !file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only .xlsx, .xls, or .csv files are supported.");
        }

        using var stream = file.OpenReadStream();
        using var workbook = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? GetCsvWorkbook(stream)
            : new XLWorkbook(stream);

        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet is null)
            return (0, 0, 0);

        var rows = worksheet.Rows().ToList();
        if (rows.Count == 0)
            return (0, 0, 0);

        var headerRow = rows.First();
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headerIndex = 1;

        foreach (var cell in headerRow.Cells())
        {
            var value = cell.GetString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                headers[NormalizeHeader(value)] = cell.Address.ColumnNumber - 1;
            }

            headerIndex++;
        }

        if (headers.Count == 0)
            return (0, 0, 0);

        var imported = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var row in rows.Skip(1))
        {
            var transactionIdCell = GetCell(row, headers, "TransactionID");
            if (transactionIdCell is null || transactionIdCell.IsEmpty())
            {
                skipped++;
                continue;
            }

            var transactionId = TryParseLong(transactionIdCell);
            if (transactionId <= 0)
            {
                skipped++;
                continue;
            }

            var sale = new NayaxSales
            {
                TransactionID = transactionId,
                TransactionStatusId = TryParseInt(GetCell(row, headers, "TransactionStatusId")),
                MachineID = TryParseLong(GetCell(row, headers, "MachineID")),
                NayaxProductId = TryParseLongOrNull(GetCell(row, headers, "NayaxProductId")),
                MachineName = GetCellValue(row, headers, "MachineName"),
                SettlementValue = TryParseDecimal(GetCell(row, headers, "SettlementValue")) ?? 0m,
                PaymentMethod = GetCellValue(row, headers, "PaymentMethod"),
                ProductName = GetCellValue(row, headers, "ProductName"),
                Quantity = TryParseDecimal(GetCell(row, headers, "Quantity")) ?? 0m,
                MachineAuthorizationTime = TryParseDateTime(GetCell(row, headers, "MachineAuthorizationTime")) ?? DateTime.MinValue
            };

            if (sale.MachineID <= 0 || sale.MachineAuthorizationTime == DateTime.MinValue)
            {
                skipped++;
                continue;
            }

            var existing = await _db.NayaxSales.FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, ct);
            if (existing is null)
            {
                _db.NayaxSales.Add(sale);
                await _saleCosting.CostSaleAsync(sale, cancellationToken: ct);
                imported++;
            }
            else
            {
                existing.MachineID = sale.MachineID;
                existing.TransactionStatusId = sale.TransactionStatusId;
                existing.NayaxProductId = sale.NayaxProductId;
                existing.MachineName = sale.MachineName;
                existing.SettlementValue = sale.SettlementValue;
                existing.PaymentMethod = sale.PaymentMethod;
                existing.ProductName = sale.ProductName;
                existing.Quantity = sale.Quantity;
                existing.MachineAuthorizationTime = sale.MachineAuthorizationTime;
                if (!NayaxTransactionStatusClassifier.IsCompletedSale(existing) ||
                    existing.CostingStatus != SaleCostingStatus.Costed)
                    await _saleCosting.CostSaleAsync(existing, cancellationToken: ct);
                updated++;
            }
        }

        if (imported > 0 || updated > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return (imported, updated, skipped);
    }

    private static IXLWorkbook GetCsvWorkbook(Stream stream)
    {
        var csvPath = Path.GetTempFileName();
        using (var fs = File.Create(csvPath))
        {
            stream.CopyTo(fs);
        }

        return new XLWorkbook(csvPath);
    }

    private static string NormalizeHeader(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized;
    }

    private static IXLCell? GetCell(IXLRow row, Dictionary<string, int> headers, string key)
    {
        if (!headers.TryGetValue(NormalizeHeader(key), out var index))
        {
            return null;
        }

        var cells = row.Cells().ToList();
        if (index >= cells.Count)
        {
            return null;
        }

        return cells[index];
    }

    private static string? GetCellValue(IXLRow row, Dictionary<string, int> headers, string key)
    {
        var cell = GetCell(row, headers, key);
        return cell is null || cell.IsEmpty() ? null : cell.GetString().Trim();
    }

    private static long TryParseLong(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return 0;
        var text = cell.GetString().Trim();
        if (long.TryParse(text, out var value)) return value;
        if (double.TryParse(text, out var number)) return Convert.ToInt64(number);
        return 0;
    }

    private static long? TryParseLongOrNull(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        var text = cell.GetString().Trim();
        if (long.TryParse(text, out var value)) return value;
        if (double.TryParse(text, out var number)) return Convert.ToInt64(number);
        return null;
    }

    private static decimal? TryParseDecimal(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        var text = cell.GetString().Trim();
        if (decimal.TryParse(text, out var value)) return value;
        if (double.TryParse(text, out var number)) return Convert.ToDecimal(number);
        return null;
    }

    private static int? TryParseInt(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        var text = cell.GetString().Trim();
        if (int.TryParse(text, out var value)) return value;
        if (double.TryParse(text, out var number)) return Convert.ToInt32(number);
        return null;
    }

    private static DateTime? TryParseDateTime(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;

        var value = cell.Value;
        if (value.IsDateTime) return value.GetDateTime();
        if (value.IsNumber) return DateTime.FromOADate(value.GetNumber());

        var text = cell.GetString().Trim();
        if (DateTime.TryParseExact(text,"d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None,  out var dt)) return dt;         
        return null;
    }

    private async Task<Machine> GetMachineSalesAsync(NayaxMachine machine, List<Product> products)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var currentWeek = GetWeekToDateRange(now);
        var previousComparableWeek = GetPreviousComparableWeekRange(now);
        var lastWeek = GetWeekRange(today, -1);
        var monthToDate = GetMonthToDateRange(now);
        var twoWeeksAgo = GetWeekRange(today, -2);

        var lastSalesTask = _db.NayaxSales.Where(s => s.MachineID == machine.MachineID && s.MachineAuthorizationTime > DateTime.Now.AddMonths(-1)).ToListAsync();
        var machineProductsTask = _nayaxLynxClient.GetMachineProductsAsync(machine.MachineID);

        await Task.WhenAll(lastSalesTask, machineProductsTask);

        var lastSales = await lastSalesTask;
        var machineProducts = await machineProductsTask;

        var results = new Machine
        {
            ActorID = machine.ActorID,
            MachineID = machine.MachineID,
            MachineName = machine.MachineName,
            MachineNumber = machine.MachineNumber
        };

        var todaySales = lastSales.Where(s => s.MachineAuthorizationTime >= today &&
                                              s.MachineAuthorizationTime <= now && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var currentWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= currentWeek.Start && s.MachineAuthorizationTime <= currentWeek.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var previousComparableWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= previousComparableWeek.Start && s.MachineAuthorizationTime <= previousComparableWeek.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var lastWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= lastWeek.Start && s.MachineAuthorizationTime <= lastWeek.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var monthToDateSales = lastSales.Where(s => s.MachineAuthorizationTime >= monthToDate.Start && s.MachineAuthorizationTime <= monthToDate.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();
        var twoWeeksAgoSales = lastSales.Where(s => s.MachineAuthorizationTime >= twoWeeksAgo.Start && s.MachineAuthorizationTime <= twoWeeksAgo.End && NayaxTransactionStatusClassifier.IsCompletedSale(s)).ToList();

        results.CurrentWeekNetRevenue = await CalculateRevenueAsync(currentWeekSales, machineProducts, products, currentWeek.Start, currentWeek.End, machine.MachineID);
        results.PreviousComparableWeekNetRevenue = await CalculateRevenueAsync(previousComparableWeekSales, machineProducts, products, previousComparableWeek.Start, previousComparableWeek.End, machine.MachineID);
        results.LastWeekNetRevenue = await CalculateRevenueAsync(lastWeekSales, machineProducts, products, lastWeek.Start, lastWeek.End, machine.MachineID);
        results.TodayNetRevenue = await CalculateRevenueAsync(todaySales, machineProducts, products, today, now, machine.MachineID);
        results.MonthToDateNetRevenue = await CalculateRevenueAsync(monthToDateSales, machineProducts, products, monthToDate.Start, monthToDate.End, machine.MachineID);
        results.TwoWeeksAgoNetRevenue = await CalculateRevenueAsync(twoWeeksAgoSales, machineProducts, products, twoWeeksAgo.Start, twoWeeksAgo.End, machine.MachineID);

        results.TodayGrossRevenue = todaySales.Sum(s => s.SettlementValue);
        results.CurrentWeekGrossRevenue = currentWeekSales.Sum(s => s.SettlementValue);
        results.PreviousComparableWeekGrossRevenue = previousComparableWeekSales.Sum(s => s.SettlementValue);
        results.LastWeekGrossRevenue = lastWeekSales.Sum(s => s.SettlementValue);
        results.MonthToDateGrossRevenue = monthToDateSales.Sum(s => s.SettlementValue);
        results.TwoWeeksAgoGrossRevenue = twoWeeksAgoSales.Sum(s => s.SettlementValue);

        return results;
    }

    private async Task SaveMachinesLastSalesAsync(List<long> machineIds, CancellationToken ct = default)
    {
        foreach (long machineId in machineIds)
        {
            var sales = await _nayaxLynxClient.GetMachineLastSalesAsync(machineId, ct);

            foreach (var sale in sales)
            {
                var existing = await _db.NayaxSales.FindAsync(sale.TransactionID);
                if (existing is null)
                {
                    _db.NayaxSales.Add(new NayaxSales
                    {
                        TransactionID = sale.TransactionID,
                        TransactionStatusId = sale.TransactionStatusId ?? 12,
                        MachineID = sale.MachineID,
                        NayaxProductId = sale.NayaxProductId,
                        MachineName = sale.MachineName,
                        SettlementValue = sale.SettlementValue,
                        PaymentMethod = sale.PaymentMethod,
                        ProductName = sale.ProductName,
                        Quantity = sale.Quantity,
                        MachineAuthorizationTime = sale.MachineAuthorizationTime
                    });
                    var added = _db.NayaxSales.Local.Last();
                    await _saleCosting.CostSaleAsync(added, cancellationToken: ct);
                }
                else
                {
                    existing.TransactionStatusId = sale.TransactionStatusId ?? 12;
                }
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task<decimal> CalculateRevenueAsync(List<NayaxSales> sales, List<NayaxMachineProduct> machineProducts, List<Product> products, DateTime from, DateTime to, long machineId)
    {
        decimal totalRevenue = 0;
        decimal machineCommission = machineProducts
            .FirstOrDefault(x => x.CommissionValue.HasValue && x.CommissionValue.Value > 0)
            ?.CommissionValue ?? 0m;

        foreach (var sale in sales)
        {
            Product? product = null;
            if (sale.NayaxProductId != null)
            {
                product = products.SingleOrDefault(p => p.Id == sale.NayaxProductId);
            }
            else
            {
                var salesName = sale.ProductName?.Split("(").First();
                product = products.SingleOrDefault(p => p.Name == salesName);
            }

            if (product != null)
            {
                decimal productCost = sale.CostOfGoodsSold ?? 0m;
                decimal commission = machineCommission != 0 ? sale.SettlementValue * machineCommission / 100 : 0;
                totalRevenue += sale.SettlementValue - (productCost * (sale.Quantity == 0 ? 1 : sale.Quantity)) - commission;
            }
        }
        var fees = await _nayaxProcessingFees.GetProcessingFeesAsync(from, to, machineId);
        return totalRevenue - fees.TotalFeeIncGst;
    }

    public static (DateTime Start, DateTime End) GetWeekRange(DateTime referenceDate, int weeksOffset = 0)
    {
        DateTime date = referenceDate.Date.AddDays(weeksOffset * 7);
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        DateTime startOfWeek = date.AddDays(-diff);
        DateTime endOfWeek = startOfWeek.AddDays(7).AddMilliseconds(-1);
        return (startOfWeek, endOfWeek);
    }

    public static (DateTime Start, DateTime End) GetWeekToDateRange(DateTime referenceDate)
    {
        var start = GetWeekRange(referenceDate.Date).Start;
        return (start, referenceDate);
    }

    public static (DateTime Start, DateTime End) GetPreviousComparableWeekRange(DateTime referenceDate)
    {
        var current = GetWeekToDateRange(referenceDate);
        return (current.Start.AddDays(-7), current.End.AddDays(-7));
    }

    public static (DateTime Start, DateTime End) GetMonthToDateRange(DateTime referenceDate)
    {
        return (new DateTime(referenceDate.Year, referenceDate.Month, 1), referenceDate);
    }
}
