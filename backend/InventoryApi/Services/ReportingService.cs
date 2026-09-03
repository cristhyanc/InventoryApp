using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed class ReportingService : IReportingService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient? _nayaxLynxClient;

    public ReportingService(AppDbContext db, INayaxLynxClient? nayaxLynxClient = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
    }

    public Task<BookkeepingReportDto> GetBookkeeping(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        GetBookkeepingAsync(filter, cancellationToken);
    public Task<DailyReportDto> GetDaily(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        GetDailyAsync(filter, cancellationToken);
    public Task<ReconciliationReportDto> GetReconciliation(ReportingFilterDto filter, decimal tolerance = 0.01m, CancellationToken cancellationToken = default) =>
        GetReconciliationAsync(filter, tolerance, cancellationToken);
    public Task<MachineProfitabilityReportDto> GetMachineProfitability(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        GetMachineProfitabilityAsync(filter, cancellationToken);
    public Task<ProductProfitabilityReportDto> GetProductProfitability(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        GetProductProfitabilityAsync(filter, cancellationToken);
    public Task<GstAccountingAidDto> GetGst(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        GetGstAsync(filter, cancellationToken);
    public Task<DashboardReportDto> GetDashboard(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        GetDashboardAsync(filter, cancellationToken);

    public async Task<BookkeepingReportDto> GetBookkeepingAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var sales = await SalesQuery(range, MachineId(filter))
            .GroupBy(_ => 1)
            .Select(g => new { Sales = g.Sum(x => x.SettlementValue), Quantity = g.Sum(x => x.Quantity) })
            .SingleOrDefaultAsync(cancellationToken) ?? new { Sales = 0m, Quantity = 0m };

        var cost = await CostQuery(range, MachineId(filter)).Select(x => x.Cost).SumAsync(cancellationToken);
        var receiptCosts = await ReceiptCostsAsync(range, cancellationToken);
        var imported = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, MachineId(filter), cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(range, MachineId(filter), commissions, cancellationToken);
        var fees = imported.FeesExGst;
        var feesIncludingGst = imported.FeesIncludingGst;
        var netSettlement = imported.HasNetSettlement ? imported.NetSettlement : sales.Sales - feesIncludingGst;
        var netProfit = sales.Sales - cost - feesIncludingGst - siteCommission - receiptCosts.Total;
        var gstOnFees = imported.ContainsGstClassification
            ? imported.GstOnFees
            : imported.GstOnFees;
        var quality = Quality(imported.ContainsRows, imported.ContainsGstClassification, false,
            imported.MachineFilterMatched ? null : filter.MachineId.HasValue
                ? "No reimbursement device row matched the selected machine ID."
                : null);
        return new BookkeepingReportDto(range.From, range.ToDate, AustralianFyHelper.Label(range.From),
            sales.Sales, cost, ReportingCalculations.GrossProfit(sales.Sales, cost), fees, netSettlement,
            ReportingCalculations.GstFromInclusive(sales.Sales), gstOnFees, quality, siteCommission, netProfit,
            ReportingCalculations.MarginPercent(sales.Sales, cost + feesIncludingGst + siteCommission + receiptCosts.Total),
            fees, feesIncludingGst, receiptCosts.Delivery, receiptCosts.Package, receiptCosts.Total);
    }

    public async Task<DailyReportDto> GetDailyAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var rows = await CostQuery(range, MachineId(filter))
            .GroupBy(x => x.MachineAuthorizationTime.Date)
            .Select(g => new
            {
                Date = g.Key,
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Sum(x => x.Quantity),
                Cost = g.Sum(x => x.Cost),
                Transactions = g.Count()
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);
        return new DailyReportDto(range.From, range.ToDate,
            rows.Select(x => new DailyReportRowDto(x.Date, x.Sales, x.Quantity, x.Cost, ReportingCalculations.GrossProfit(x.Sales, x.Cost), x.Transactions)).ToList(),
            Quality(false, false, false));
    }

    public async Task<ReconciliationReportDto> GetReconciliationAsync(ReportingFilterDto filter, decimal tolerance = 0.01m, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var nayax = await SalesQuery(range, MachineId(filter)).SumAsync(x => x.SettlementValue, cancellationToken);
        var imported = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var difference = nayax - imported.Settlement;
        var quality = Quality(imported.ContainsRows, imported.ContainsGstClassification, false,
            !imported.ContainsRows ? "No imported reimbursement row matched the requested period." :
            imported.MachineFilterMatched ? null :
            filter.MachineId.HasValue ? "No reimbursement device row matched the selected machine ID." : null);
        return new ReconciliationReportDto(range.From, range.ToDate, nayax, imported.Settlement,
            difference, Math.Abs(tolerance), Math.Abs(difference) <= Math.Abs(tolerance), quality);
    }

    public async Task<MachineProfitabilityReportDto> GetMachineProfitabilityAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var rows = await CostQuery(range, MachineId(filter))
            .GroupBy(x => new { x.MachineID, x.MachineName })
            .Select(g => new
            {
                g.Key.MachineID,
                g.Key.MachineName,
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Sum(x => x.Quantity),
                Cost = g.Sum(x => x.Cost),
                Transactions = g.Count()
            }).OrderByDescending(x => x.Sales).ToListAsync(cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, MachineId(filter), cancellationToken);
        return new MachineProfitabilityReportDto(range.From, range.ToDate,
            rows.Select(x => new MachineProfitabilityRowDto(x.MachineID, x.MachineName ?? $"Machine {x.MachineID}",
                x.Sales, x.Quantity, x.Cost, ReportingCalculations.GrossProfit(x.Sales, x.Cost), ReportingCalculations.MarginPercent(x.Sales, x.Cost), x.Transactions,
                x.Sales * commissions.GetValueOrDefault(x.MachineID).Percent / 100m,
                x.Sales - x.Cost - x.Sales * commissions.GetValueOrDefault(x.MachineID).Percent / 100m,
                ReportingCalculations.MarginPercent(x.Sales, x.Cost + x.Sales * commissions.GetValueOrDefault(x.MachineID).Percent / 100m),
                commissions.GetValueOrDefault(x.MachineID).Percent)).ToList(),
            Quality(false, false, false));
    }

    public async Task<ProductProfitabilityReportDto> GetProductProfitabilityAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var products = await _db.Products.AsNoTracking().Include(p => p.Category).ToDictionaryAsync(p => p.Id, cancellationToken);
        var rows = await SalesQuery(range, MachineId(filter))
            .GroupBy(x => new { x.NayaxProductId, x.ProductName })
            .Select(g => new
            {
                g.Key.NayaxProductId,
                g.Key.ProductName,
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Sum(x => x.Quantity),
                Transactions = g.Count()
            }).OrderByDescending(x => x.Sales).ToListAsync(cancellationToken);

        var result = rows.Select(x =>
        {
            products.TryGetValue(x.NayaxProductId ?? 0, out var product);
            var cost = product is null ? 0m : product.UnitPrice * x.Quantity;
            var name = product?.Name ?? (string.IsNullOrWhiteSpace(x.ProductName) ? "Unmapped product" : x.ProductName);
            return new ProductProfitabilityRowDto(x.NayaxProductId, name, product?.Category?.Name,
                x.Sales, x.Quantity, cost, ReportingCalculations.GrossProfit(x.Sales, cost), ReportingCalculations.MarginPercent(x.Sales, cost),
                x.Transactions, product is null, product is not null);
        }).ToList();
        var quality = Quality(false, false, result.Any(x => x.IsUnmapped),
            result.Any(x => x.IsUnmapped) ? "One or more sales could not be mapped to a Product." : null);
        return new ProductProfitabilityReportDto(range.From, range.ToDate, result, quality);
    }

    public async Task<GstAccountingAidDto> GetGstAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var bookkeeping = await GetBookkeepingAsync(filter, cancellationToken);
        var imported = await ImportedSummaryAsync(ResolveRange(filter), MachineId(filter), cancellationToken);
        var quality = Quality(imported.ContainsRows, imported.ContainsGstClassification, false);
        var taxableSales = bookkeeping.Sales - bookkeeping.GstOnSales;
        var taxableFees = bookkeeping.NayaxFeesExGst;
        return new GstAccountingAidDto(bookkeeping.From, bookkeeping.To, taxableSales,
            bookkeeping.GstOnSales, taxableFees, bookkeeping.GstOnFees,
            bookkeeping.GstOnSales - bookkeeping.GstOnFees, quality);
    }

    public async Task<DashboardReportDto> GetDashboardAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var summary = await CostQuery(range, MachineId(filter)).GroupBy(_ => 1)
            .Select(g => new
            {
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Sum(x => x.Quantity),
                Cost = g.Sum(x => x.Cost),
                Transactions = g.Count(),
                Machines = g.Select(x => x.MachineID).Distinct().Count(),
                Products = g.Select(x => x.NayaxProductId).Where(x => x.HasValue).Distinct().Count()
            }).SingleOrDefaultAsync(cancellationToken);
        var productReport = await GetProductProfitabilityAsync(filter, cancellationToken);
        var imported = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, MachineId(filter), cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(range, MachineId(filter), commissions, cancellationToken);
        var receiptCosts = await ReceiptCostsAsync(range, cancellationToken);
        var fees = imported.FeesIncludingGst;
        var netProfit = (summary?.Sales ?? 0m) - (summary?.Cost ?? 0m) - fees - siteCommission - receiptCosts.Total;
        return new DashboardReportDto(range.From, range.ToDate, summary?.Sales ?? 0m,
            ReportingCalculations.GrossProfit(summary?.Sales ?? 0m, summary?.Cost ?? 0m), summary?.Transactions ?? 0,
            summary?.Quantity ?? 0m, summary?.Machines ?? 0, summary?.Products ?? 0,
            productReport.Rows.Count(x => x.IsUnmapped), productReport.DataQuality, fees,
            imported.HasNetSettlement ? imported.NetSettlement : (summary?.Sales ?? 0m) - fees,
            siteCommission, netProfit,
            ReportingCalculations.MarginPercent(summary?.Sales ?? 0m, (summary?.Cost ?? 0m) + fees + siteCommission + receiptCosts.Total),
            imported.FeesExGst, receiptCosts.Delivery, receiptCosts.Package, receiptCosts.Total);
    }

    public async Task<byte[]> ExportCsvAsync(string report, ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var rows = await ExportRowsAsync(report, filter, cancellationToken);
        var builder = new StringBuilder();
        foreach (var row in rows)
            builder.AppendLine(string.Join(",", row.Select(Csv)));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public async Task<byte[]> ExportXlsxAsync(string report, ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var rows = await ExportRowsAsync(report, filter, cancellationToken);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Report");
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                sheet.Cell(r + 1, c + 1).Value = rows[r][c];
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<List<List<string>>> ExportRowsAsync(string report, ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        report = report.Trim().ToLowerInvariant();
        if (report is "daily")
        {
            var value = await GetDailyAsync(filter, cancellationToken);
            return new[] { new List<string> { "Date", "Sales", "Quantity", "CostOfGoods", "GrossProfit", "Transactions" } }
                .Concat(value.Rows.Select(x => new List<string> { x.Date.ToString("yyyy-MM-dd"), x.Sales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), x.CostOfGoods.ToString(CultureInfo.InvariantCulture), x.GrossProfit.ToString(CultureInfo.InvariantCulture), x.TransactionCount.ToString(CultureInfo.InvariantCulture) })).ToList();
        }
        if (report is "bookkeeping")
        {
            var value = await GetBookkeepingAsync(filter, cancellationToken);
            return new List<List<string>>
            {
                new() { "From", "To", "FinancialYear", "Sales", "CostOfGoods", "GrossProfit", "NayaxFeesExGst", "NayaxFeesIncludingGst", "DeliveryCosts", "PackageCosts", "OtherOperatingExpenses", "NetSettlement", "SiteCommission", "NetProfit", "GstOnSales", "GstOnFees" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.FinancialYear, value.Sales.ToString(CultureInfo.InvariantCulture), value.CostOfGoods.ToString(CultureInfo.InvariantCulture), value.GrossProfit.ToString(CultureInfo.InvariantCulture), value.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), value.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture), value.DeliveryCosts.ToString(CultureInfo.InvariantCulture), value.PackageCosts.ToString(CultureInfo.InvariantCulture), value.OtherOperatingExpenses.ToString(CultureInfo.InvariantCulture), value.NetSettlement.ToString(CultureInfo.InvariantCulture), value.SiteCommission.ToString(CultureInfo.InvariantCulture), value.NetProfit.ToString(CultureInfo.InvariantCulture), value.GstOnSales.ToString(CultureInfo.InvariantCulture), value.GstOnFees.ToString(CultureInfo.InvariantCulture) }
            };
        }
        if (report is "reconciliation")
        {
            var value = await GetReconciliationAsync(filter, cancellationToken: cancellationToken);
            return new List<List<string>>
            {
                new() { "From", "To", "NayaxSales", "ImportedReimbursement", "Difference", "Tolerance", "IsMatch" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.NayaxSales.ToString(CultureInfo.InvariantCulture), value.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), value.Difference.ToString(CultureInfo.InvariantCulture), value.Tolerance.ToString(CultureInfo.InvariantCulture), value.IsMatch.ToString() }
            };
        }
        if (report is "machine-profitability" or "machines")
        {
            var value = await GetMachineProfitabilityAsync(filter, cancellationToken);
            return new[] { new List<string> { "MachineId", "MachineName", "Sales", "Quantity", "CostOfGoods", "GrossProfit", "CommissionPercent", "SiteCommission", "NetProfit", "NetMarginPercent", "Transactions" } }
                .Concat(value.Rows.Select(x => new List<string> { x.MachineId.ToString(), x.MachineName, x.Sales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), x.CostOfGoods.ToString(CultureInfo.InvariantCulture), x.GrossProfit.ToString(CultureInfo.InvariantCulture), x.CommissionPercent.ToString(CultureInfo.InvariantCulture), x.SiteCommission.ToString(CultureInfo.InvariantCulture), x.NetProfit.ToString(CultureInfo.InvariantCulture), x.NetMarginPercent.ToString(CultureInfo.InvariantCulture), x.TransactionCount.ToString(CultureInfo.InvariantCulture) })).ToList();
        }
        if (report is "gst" or "gst-accounting")
        {
            var value = await GetGstAsync(filter, cancellationToken);
            return new List<List<string>>
            {
                new() { "From", "To", "TaxableSales", "GstOnSales", "TaxableFees", "GstOnFees", "NetGst" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.TaxableSales.ToString(CultureInfo.InvariantCulture), value.GstOnSales.ToString(CultureInfo.InvariantCulture), value.TaxableFees.ToString(CultureInfo.InvariantCulture), value.GstOnFees.ToString(CultureInfo.InvariantCulture), value.NetGst.ToString(CultureInfo.InvariantCulture) }
            };
        }
        if (report is "dashboard")
        {
            var value = await GetDashboardAsync(filter, cancellationToken);
            return new List<List<string>>
            {
                new() { "From", "To", "Sales", "GrossProfit", "Transactions", "Quantity", "MachineCount", "ProductCount", "UnmappedProductCount" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.Sales.ToString(CultureInfo.InvariantCulture), value.GrossProfit.ToString(CultureInfo.InvariantCulture), value.Transactions.ToString(CultureInfo.InvariantCulture), value.Quantity.ToString(CultureInfo.InvariantCulture), value.MachineCount.ToString(), value.ProductCount.ToString(), value.UnmappedProductCount.ToString() }
            };
        }
        var products = await GetProductProfitabilityAsync(filter, cancellationToken);
        return new[] { new List<string> { "Product", "Sales", "Quantity", "CostOfGoods", "GrossProfit", "MarginPercent", "Transactions", "Unmapped" } }
            .Concat(products.Rows.Select(x => new List<string> { x.ProductName, x.Sales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), x.CostOfGoods.ToString(CultureInfo.InvariantCulture), x.GrossProfit.ToString(CultureInfo.InvariantCulture), x.MarginPercent.ToString(CultureInfo.InvariantCulture), x.TransactionCount.ToString(CultureInfo.InvariantCulture), x.IsUnmapped.ToString() })).ToList();
    }

    private IQueryable<NayaxSales> SalesQuery(DateRange range, long? machineId) =>
        _db.NayaxSales.AsNoTracking().Where(x => x.MachineAuthorizationTime >= range.From && x.MachineAuthorizationTime < range.EndExclusive &&
            (!machineId.HasValue || x.MachineID == machineId.Value));

    private IQueryable<SaleCost> CostQuery(DateRange range, long? machineId) =>
        from sale in SalesQuery(range, machineId)
        join product in _db.Products.AsNoTracking() on sale.NayaxProductId equals product.Id into productJoin
        from product in productJoin.DefaultIfEmpty()
        select new SaleCost
        {
            MachineID = sale.MachineID, MachineName = sale.MachineName, NayaxProductId = sale.NayaxProductId,
            ProductName = sale.ProductName, SettlementValue = sale.SettlementValue, Quantity = sale.Quantity,
            MachineAuthorizationTime = sale.MachineAuthorizationTime, Cost = product == null ? 0m : product.UnitPrice * sale.Quantity
        };

    private async Task<ReceiptCostSummary> ReceiptCostsAsync(DateRange range, CancellationToken cancellationToken)
    {
        var costs = await _db.Receipts.AsNoTracking()
            .Where(x => x.PurchaseDate >= range.From && x.PurchaseDate < range.EndExclusive)
            .GroupBy(_ => 1)
            .Select(g => new ReceiptCostSummary(
                g.Sum(x => x.DeliveryCost ?? 0m),
                g.Sum(x => x.PackageCost ?? 0m)))
            .SingleOrDefaultAsync(cancellationToken);
        return costs;
    }

    private async Task<ImportedSummary> ImportedSummaryAsync(DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate < range.EndExclusive && x.ReimbursementEndDate >= range.From);
        var reimbursementRows = await reimbursements
            .Select(x => new ImportedReimbursementRow
            {
                Id = x.Id,
                Total = x.Total
            })
            .ToListAsync(cancellationToken);
        if (reimbursementRows.Count == 0)
            return new ImportedSummary(0m, 0m, 0m, 0m, 0m, false, false, false, false);

        var reimbursementIds = reimbursementRows.Select(x => x.Id).ToList();
        var feeRows = await _db.ImportedFees.AsNoTracking()
            .Where(x => reimbursementIds.Contains(x.ImportedReimbursementId))
            .Select(x => new ImportedFeeRow
            {
                ReimbursementId = x.ImportedReimbursementId,
                IsPreviousPeriod = x.IsPreviousPeriod,
                TotalSum = x.TotalSum,
                TotalSumWithVat = x.TotalSumWithVat,
                VatPercentage = x.VatPercentage
            })
            .ToListAsync(cancellationToken);
        var deviceRows = await _db.ImportedReimbursementDevices.AsNoTracking()
            .Where(x => reimbursementIds.Contains(x.ImportedReimbursementId))
            .Select(x => new ImportedDeviceRow
            {
                ReimbursementId = x.ImportedReimbursementId,
                EntityId = x.EntityId,
                MachineNumber = x.MachineNumber,
                Gross = x.TotalBillableTransactionAmount ?? 0m,
                NetAmount = x.NetAmount,
                HasNetAmount = x.NetAmount.HasValue
            })
            .ToListAsync(cancellationToken);
        var entityIds = deviceRows
            .Where(x => x.EntityId != null)
            .Select(x => x.EntityId!)
            .Distinct()
            .ToList();
        var paymentRows = await _db.ImportedDevicePayments.AsNoTracking()
            .Where(x => reimbursementIds.Contains(x.ImportedReimbursementId) && entityIds.Contains(x.EntityId!))
            .Select(x => new ImportedPaymentRow
            {
                ReimbursementId = x.ImportedReimbursementId,
                EntityId = x.EntityId,
                PaymentMethodDescription = x.PaymentMethodDescription,
                RecognitionDescription = x.RecognitionDescription,
                Amount = x.TotalSum ?? 0m,
                Fees = (x.ProcessingFees ?? 0m) + (x.ServiceFees ?? 0m)
            })
            .ToListAsync(cancellationToken);

        var rows = reimbursementRows.Select(x => new ImportedReportRow
        {
            Id = x.Id,
            Settlement = x.Total ?? 0m,
            FeesExGst = feeRows.Where(f => f.ReimbursementId == x.Id && !f.IsPreviousPeriod).Sum(f => f.TotalSum ?? 0m),
            FeesIncludingGst = feeRows.Where(f => f.ReimbursementId == x.Id && !f.IsPreviousPeriod).Sum(f => f.TotalSumWithVat ?? f.TotalSum ?? 0m),
            Gst = feeRows.Where(f => f.ReimbursementId == x.Id && !f.IsPreviousPeriod).Sum(f =>
                f.TotalSumWithVat.HasValue && f.TotalSum.HasValue
                    ? f.TotalSumWithVat.Value - f.TotalSum.Value
                    : f.TotalSumWithVat.HasValue && f.VatPercentage.HasValue
                        ? f.TotalSumWithVat.Value * f.VatPercentage.Value / (100m + f.VatPercentage.Value)
                        : 0m),
            HasNet = deviceRows.Any(d => d.ReimbursementId == x.Id && d.HasNetAmount),
            HasGst = feeRows.Any(f => f.ReimbursementId == x.Id && f.VatPercentage.HasValue),
            Devices = deviceRows.Where(d => d.ReimbursementId == x.Id).Select(d => new ImportedReportDevice
            {
                EntityId = d.EntityId,
                MachineNumber = d.MachineNumber,
                Gross = d.Gross,
                NetAmount = d.NetAmount,
                Payments = paymentRows.Where(p => p.ReimbursementId == x.Id && p.EntityId == d.EntityId)
                    .Select(p => new ImportedPaymentNet(p.PaymentMethodDescription, p.RecognitionDescription, p.Amount, p.Fees))
                    .ToList()
            }).ToList()
        }).ToList();

        decimal NetAmount(IEnumerable<ImportedPaymentNet> payments, decimal? fallback) =>
            payments.Any()
                ? payments.Where(p => !IsCashPayment(p.PaymentMethodDescription, p.RecognitionDescription))
                    .Sum(p => p.Amount - p.Fees)
                : fallback ?? 0m;

        if (!machineId.HasValue)
            return new ImportedSummary(rows.Sum(x => x.Settlement), rows.Sum(x => x.FeesExGst),
                rows.Sum(x => x.FeesIncludingGst), rows.Sum(x => x.Gst),
                rows.SelectMany(x => x.Devices).Sum(d => NetAmount(d.Payments, d.NetAmount)),
                rows.Count != 0, rows.Any(x => x.HasGst), rows.Any(x => x.HasNet), rows.Count != 0);

        var matchingDevices = rows.SelectMany(x => x.Devices)
            .Where(d => long.TryParse(d.MachineNumber, out var parsed) && parsed == machineId.Value)
            .ToList();
        var matchingReimbursementIds = rows
            .Where(row => row.Devices.Any(device => long.TryParse(device.MachineNumber, out var parsed) && parsed == machineId.Value))
            .Select(row => row.Id)
            .ToHashSet();
        var matchingRows = rows.Where(row => matchingReimbursementIds.Contains(row.Id)).ToList();
        return new ImportedSummary(matchingDevices.Sum(x => x.Gross), matchingRows.Sum(x => x.FeesExGst),
            matchingRows.Sum(x => x.FeesIncludingGst), matchingRows.Sum(x => x.Gst),
            matchingDevices.Sum(x => NetAmount(x.Payments, x.NetAmount)),
            rows.Count != 0, matchingRows.Any(x => x.HasGst), matchingDevices.Any(x => x.NetAmount.HasValue), matchingDevices.Count != 0);
    }

    private static bool IsCashPayment(string? paymentMethod, string? recognition) =>
        (paymentMethod ?? string.Empty).Contains("cash", StringComparison.OrdinalIgnoreCase) ||
        (recognition ?? string.Empty).Contains("cash", StringComparison.OrdinalIgnoreCase);

    private async Task<Dictionary<long, MachineCommission>> GetMachineCommissionsAsync(DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        if (_nayaxLynxClient is null) return new();
        var machineIds = await SalesQuery(range, machineId).Select(x => x.MachineID).Distinct().ToListAsync(cancellationToken);
        var results = await Task.WhenAll(machineIds.Select(async id =>
        {
            var products = await _nayaxLynxClient.GetMachineProductsAsync(id, cancellationToken);
            var commission = products.FirstOrDefault(x => x.CommissionValue.HasValue)?.CommissionValue ?? 0m;
            return (id, commission);
        }));
        return results.ToDictionary(x => x.id, x => new MachineCommission(x.commission));
    }

    private async Task<decimal> GetSiteCommissionAsync(DateRange range, long? machineId, Dictionary<long, MachineCommission> commissions, CancellationToken cancellationToken)
    {
        var salesByMachine = await SalesQuery(range, machineId)
            .GroupBy(x => x.MachineID)
            .Select(g => new { MachineId = g.Key, Sales = g.Sum(x => x.SettlementValue) })
            .ToListAsync(cancellationToken);
        return salesByMachine.Sum(x => x.Sales * commissions.GetValueOrDefault(x.MachineId).Percent / 100m);
    }

    private static ReportingDataQualityDto Quality(bool importedRows, bool gstClassification, bool unmapped, string? note = null) =>
        new(true, true, true, true, unmapped,
            new[] { "NayaxSales does not persist transaction status.", "Historical product cost is represented by the current Product.UnitPrice.", "Commission is read from Nayax machine products; the first product with a commission defines the machine rate.", "GST classification is not persisted on sales; GST amounts are an indicative 10% inclusive calculation." }
                .Concat(note is null ? Array.Empty<string>() : new[] { note }).ToList());

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static DateRange ResolveRange(ReportingFilterDto filter)
    {
        var from = (filter.From ?? filter.StartDate)?.Date ?? new DateTime(1900, 1, 1);
        var to = (filter.To ?? filter.EndDate)?.Date ?? new DateTime(9999, 12, 30);
        if (!string.IsNullOrWhiteSpace(filter.FinancialYear) && AustralianFyHelper.TryParse(filter.FinancialYear, out var fyFrom))
        {
            from = fyFrom;
            to = fyFrom.AddYears(1).AddDays(-1);
        }
        if (to < from) (from, to) = (to, from);
        return new DateRange(from, to);
    }

    private static long? MachineId(ReportingFilterDto filter) => filter.MachineId ?? filter.MachineID;

    private readonly record struct DateRange(DateTime From, DateTime ToDate)
    {
        public DateTime EndExclusive => ToDate.Date.AddDays(1);
    }

    private sealed class SaleCost
    {
        public long MachineID { get; set; }
        public string? MachineName { get; set; }
        public long? NayaxProductId { get; set; }
        public string? ProductName { get; set; }
        public decimal SettlementValue { get; set; }
        public decimal Quantity { get; set; }
        public DateTime MachineAuthorizationTime { get; set; }
        public decimal Cost { get; set; }
    }

    private sealed class ImportedReimbursementRow
    {
        public int Id { get; set; }
        public decimal? Total { get; set; }
    }

    private sealed class ImportedFeeRow
    {
        public int ReimbursementId { get; set; }
        public bool IsPreviousPeriod { get; set; }
        public decimal? TotalSum { get; set; }
        public decimal? TotalSumWithVat { get; set; }
        public decimal? VatPercentage { get; set; }
    }

    private sealed class ImportedDeviceRow
    {
        public int ReimbursementId { get; set; }
        public string? EntityId { get; set; }
        public string? MachineNumber { get; set; }
        public decimal Gross { get; set; }
        public decimal? NetAmount { get; set; }
        public bool HasNetAmount { get; set; }
    }

    private sealed class ImportedPaymentRow
    {
        public int ReimbursementId { get; set; }
        public string? EntityId { get; set; }
        public string? PaymentMethodDescription { get; set; }
        public string? RecognitionDescription { get; set; }
        public decimal Amount { get; set; }
        public decimal Fees { get; set; }
    }

    private sealed class ImportedReportRow
    {
        public int Id { get; set; }
        public decimal Settlement { get; set; }
        public decimal FeesExGst { get; set; }
        public decimal FeesIncludingGst { get; set; }
        public decimal Gst { get; set; }
        public bool HasNet { get; set; }
        public bool HasGst { get; set; }
        public List<ImportedReportDevice> Devices { get; set; } = new();
    }

    private sealed class ImportedReportDevice
    {
        public string? EntityId { get; set; }
        public string? MachineNumber { get; set; }
        public decimal Gross { get; set; }
        public decimal? NetAmount { get; set; }
        public List<ImportedPaymentNet> Payments { get; set; } = new();
    }

    private readonly record struct MachineCommission(decimal Percent);
    private readonly record struct ReceiptCostSummary(decimal Delivery, decimal Package)
    {
        public decimal Total => Delivery + Package;
    }

    private readonly record struct ImportedPaymentNet(
        string? PaymentMethodDescription,
        string? RecognitionDescription,
        decimal Amount,
        decimal Fees);

    private readonly record struct ImportedSummary(decimal Settlement, decimal FeesExGst, decimal FeesIncludingGst, decimal GstOnFees, decimal NetSettlement, bool ContainsRows, bool ContainsGstClassification, bool HasNetSettlement, bool MachineFilterMatched);
}

public static class AustralianFyHelper
{
    public static DateTime Start(DateTime date) => new(date.Month >= 7 ? date.Year : date.Year - 1, 7, 1);
    public static string Label(DateTime date) { var start = Start(date); return $"FY{start.Year}-{(start.Year + 1) % 100:00}"; }
    public static bool TryParse(string value, out DateTime start)
    {
        start = default;
        var text = value.Trim().ToUpperInvariant().Replace("FY", string.Empty).Replace("/", "-").Trim();
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(parts[0], out var year) || year < 1900) return false;
        var startYear = parts.Length > 1 && year < 100 ? year + 2000 : (parts.Length > 1 ? year : year - 1);
        if (parts.Length == 1 && year >= 1900) startYear = year - 1;
        start = new DateTime(startYear, 7, 1);
        return true;
    }

}

public static class AustralianFinancialYear
{
    public static DateTime Start(DateTime date) => AustralianFyHelper.Start(date);
    public static DateTime End(DateTime date) => Start(date).AddYears(1).AddDays(-1);
    public static string Label(DateTime date) => AustralianFyHelper.Label(date);
    public static bool TryParse(string value, out DateTime start) => AustralianFyHelper.TryParse(value, out start);
}

public static class ReportingCalculations
{
    public static decimal GrossProfit(decimal sales, decimal costOfGoods) => sales - costOfGoods;
    public static decimal MarginPercent(decimal sales, decimal costOfGoods) =>
        sales == 0m ? 0m : GrossProfit(sales, costOfGoods) / sales * 100m;
    public static decimal GstFromInclusive(decimal amount) => amount * 10m / 110m;
}
