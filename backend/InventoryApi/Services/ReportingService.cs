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
        var paymentSummary = await GetPaymentSummaryAsync(range, MachineId(filter), cancellationToken);
        var sales = paymentSummary.GrossSales;

        var cost = await CostQuery(range, MachineId(filter)).Select(x => x.Cost).SumAsync(cancellationToken);
        var receiptCosts = await ReceiptCostsAsync(range, cancellationToken);
        var imported = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, MachineId(filter), cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(range, MachineId(filter), commissions, cancellationToken);
        var fees = imported.FeesExGst;
        var feesIncludingGst = imported.FeesIncludingGst;
        var netSettlement = imported.HasNetSettlement ? imported.NetSettlement : paymentSummary.CardSales - feesIncludingGst;
        var netProfit = sales - cost - feesIncludingGst - siteCommission - receiptCosts.Total;
        var gstOnFees = imported.ContainsGstClassification
            ? imported.GstOnFees
            : imported.GstOnFees;
        var qualityNotes = new List<string>();
        if (paymentSummary.UnknownTransactions > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        if (filter.MachineId.HasValue && !imported.MachineFilterMatched)
            qualityNotes.Add("No reimbursement device row matched the selected machine ID.");
        if (imported.FeesMachineFilterLimited)
            qualityNotes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        var quality = Quality(imported.ContainsRows, imported.ContainsGstClassification, false,
            qualityNotes.Count == 0 ? null : string.Join(" ", qualityNotes));
        return new BookkeepingReportDto(range.From, range.ToDate, AustralianFyHelper.Label(range.From),
            sales, cost, ReportingCalculations.GrossProfit(sales, cost), fees, netSettlement,
            ReportingCalculations.GstFromInclusive(sales), gstOnFees, quality, siteCommission, netProfit,
            ReportingCalculations.MarginPercent(sales, cost + feesIncludingGst + siteCommission + receiptCosts.Total),
            fees, feesIncludingGst, receiptCosts.Delivery, receiptCosts.Package, receiptCosts.Total,
            paymentSummary.CardSales, paymentSummary.CashSales, paymentSummary.CardTransactions,
            paymentSummary.CashTransactions, ReportingCalculations.PercentageOf(feesIncludingGst, paymentSummary.CardSales));
    }

    public async Task<DailyReportDto> GetDailyAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var sales = await CostQuery(range, MachineId(filter)).ToListAsync(cancellationToken);
        var statusSales = await AllSalesQuery(range, MachineId(filter)).ToListAsync(cancellationToken);
        var importedByDate = await DailyImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var importedPeriod = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var rows = sales
            .GroupBy(x => x.MachineAuthorizationTime.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var grossSales = g.Sum(x => x.SettlementValue);
                var cost = g.Sum(x => x.Cost);
                var cardSales = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card)
                    .Sum(x => x.SettlementValue);
                var cashSales = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash)
                    .Sum(x => x.SettlementValue);
                var uncosted = g.Where(x => !x.HasCost).ToList();
                var unknownTransactions = g.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Unknown);
                var imported = importedByDate.TryGetValue(g.Key, out var importedValue)
                    ? importedValue
                    : (DailyImportedSummary?)null;
                var statusRows = statusSales.Where(x => x.MachineAuthorizationTime.Date == g.Key).ToList();
                var importedReimbursement = imported?.Reimbursement ?? 0m;
                var difference = cardSales - importedReimbursement;
                var hasImported = imported?.HasReimbursement ?? false;
                return new DailyReportRowDto(
                    g.Key, grossSales, g.Sum(x => x.Quantity), cost,
                    ReportingCalculations.GrossProfit(grossSales, cost), g.Count(),
                    grossSales, cardSales, cashSales,
                    ReportingCalculations.Average(grossSales, g.Count()),
                    uncosted.Count == 0, uncosted.Count, uncosted.Sum(x => x.SettlementValue),
                    ReportingCalculations.MarginPercent(grossSales, cost),
                    imported?.FeesExGst ?? 0m, imported?.FeesIncludingGst ?? 0m,
                    importedReimbursement, imported?.NetReimbursement ?? 0m,
                    hasImported && IsReconciled(difference, 0.01m),
                    ReconciliationStatus(hasImported, difference, 0.01m,
                    uncosted.Count != 0 || unknownTransactions != 0 ||
                        statusRows.Any(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) != NayaxTransactionStatus.Completed &&
                                            x.TransactionStatusId is not null),
                    imported?.HasPeriodOnlyData == true),
                    statusRows.Count(x => NayaxTransactionStatusClassifier.IsCompletedSale(x)),
                    statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
                    statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
                    statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
                    statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown));
            }).ToList();
        var totals = new DailyReportTotalsDto(
            rows.Sum(x => x.GrossSales), rows.Sum(x => x.CardSales), rows.Sum(x => x.CashSales),
            rows.Sum(x => x.Quantity), rows.Sum(x => x.CostOfGoods), rows.Sum(x => x.GrossProfit),
            rows.Sum(x => x.TransactionCount), ReportingCalculations.Average(rows.Sum(x => x.GrossSales), rows.Sum(x => x.TransactionCount)),
            rows.All(x => x.IsCogsComplete), rows.Sum(x => x.UncostedTransactionCount), rows.Sum(x => x.UncostedSalesAmount),
            ReportingCalculations.MarginPercent(rows.Sum(x => x.GrossSales), rows.Sum(x => x.CostOfGoods)),
            importedPeriod.ContainsRows ? importedPeriod.FeesExGst : rows.Sum(x => x.NayaxFeesExGst),
            importedPeriod.ContainsRows ? importedPeriod.FeesIncludingGst : rows.Sum(x => x.NayaxFeesIncludingGst),
            importedPeriod.ContainsRows ? importedPeriod.Settlement : rows.Sum(x => x.ImportedReimbursement),
            importedPeriod.ContainsRows ? importedPeriod.NetSettlement : rows.Sum(x => x.NetReimbursement),
            statusSales.Count(x => NayaxTransactionStatusClassifier.IsCompletedSale(x)),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown));
        var note = importedPeriod.FeesMachineFilterLimited
            ? "Imported fees are account-level amounts and are not allocated to a selected machine."
            : importedByDate.Values.Any(x => x.HasPeriodOnlyData)
                ? "Some reimbursements cover a period longer than one day and are not allocated to daily rows."
                : null;
        var qualityNotes = new List<string>();
        if (note is not null) qualityNotes.Add(note);
        AddStatusQualityNotes(qualityNotes, statusSales);
        var quality = Quality(importedPeriod.ContainsRows, importedPeriod.ContainsGstClassification, false,
            qualityNotes.Count == 0 ? null : string.Join(" ", qualityNotes));
        return new DailyReportDto(range.From, range.ToDate, rows,
            quality,
            totals);
    }

    public async Task<ReconciliationReportDto> GetReconciliationAsync(ReportingFilterDto filter, decimal tolerance = 0.01m, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var machineId = MachineId(filter);
        var paymentSummary = await GetPaymentSummaryAsync(range, machineId, cancellationToken);
        var sales = await SalesQuery(range, machineId).ToListAsync(cancellationToken);
        var statusSales = await AllSalesQuery(range, machineId).ToListAsync(cancellationToken);
        var reimbursements = await _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate.HasValue && x.ReimbursementEndDate.HasValue)
            .Include(x => x.Devices)
            .Include(x => x.DevicePayments)
            .Include(x => x.PaymentMethods)
            .Include(x => x.Fees)
            .ToListAsync(cancellationToken);

        // Reconciliation is deliberately period based: an overlapping reimbursement is not
        // silently attributed to a different requested period.
        var matching = reimbursements
            .Where(x => x.ReimbursementStartDate!.Value.Date >= range.From.Date &&
                x.ReimbursementEndDate!.Value.Date <= range.ToDate.Date)
            .OrderBy(x => x.Id)
            .ToList();
        var periodRows = new List<ReconciliationPeriodDto>();
        foreach (var reimbursement in matching)
        {
            var periodSales = sales.Where(x => x.MachineAuthorizationTime.Date >= reimbursement.ReimbursementStartDate!.Value.Date &&
                x.MachineAuthorizationTime.Date <= reimbursement.ReimbursementEndDate!.Value.Date).ToList();
            periodRows.Add(BuildReconciliationPeriod(reimbursement, periodSales, machineId, tolerance));
        }

        if (periodRows.Count == 0)
            periodRows.Add(BuildReconciliationPeriod(null, sales, machineId, tolerance, range.From, range.ToDate));

        var importedGross = periodRows.Sum(x => x.NayaxReportedGrossCardSales);
        var grossDifference = paymentSummary.CardSales - importedGross;
        var hasImported = matching.Count != 0;
        var aggregateWarning = periodRows.Any(x => x.GrossStatus == "Warning" || x.SettlementStatus == "Warning") ||
            statusSales.Any(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending);
        var aggregateSettlementDifference = periodRows.Sum(x => x.SettlementDifference);
        var grossStatus = StatusFor(!hasImported, grossDifference, tolerance, aggregateWarning);
        var settlementStatus = StatusFor(!hasImported, aggregateSettlementDifference, tolerance, aggregateWarning);
        var totals = new ReconciliationTotalsDto(
            paymentSummary.GrossSales, paymentSummary.CardSales, paymentSummary.CashSales,
            paymentSummary.CardSales, importedGross,
            paymentSummary.CardTransactions, periodRows.Sum(x => x.NayaxReportedCardTransactionCount),
            paymentSummary.CardTransactions - periodRows.Sum(x => x.NayaxReportedCardTransactionCount), grossDifference,
            periodRows.Sum(x => x.ProcessingFeesExGst), periodRows.Sum(x => x.FeeGst), periodRows.Sum(x => x.OtherFees),
            periodRows.Sum(x => x.Adjustments), periodRows.Sum(x => x.ExpectedNetReimbursement),
            periodRows.Sum(x => x.ActualNetReimbursement), periodRows.Sum(x => x.SettlementDifference),
            grossStatus, settlementStatus, OverallStatus(grossStatus, settlementStatus))
        {
            TotalTransactionCount = sales.Count,
            CashTransactionCount = paymentSummary.CashTransactions
        };

        var actualNet = totals.ActualNetReimbursement;
        var qualityNotes = new List<string>();
        if (!hasImported)
            qualityNotes.Add("No imported reimbursement row matched the requested start and end dates.");
        if (paymentSummary.UnknownTransactions > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        AddStatusQualityNotes(qualityNotes, statusSales);
        if (machineId.HasValue)
            qualityNotes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        qualityNotes.Add("Adjustments are unsupported by the imported reimbursement model and are treated as zero.");
        var quality = Quality(hasImported, periodRows.Any(x => x.DataQuality.GstClassificationMissing == false), false,
            string.Join(" ", qualityNotes) is { Length: > 0 } note ? note : null);
        var first = periodRows[0];
        return new ReconciliationReportDto(range.From, range.ToDate, totals.CardTransactionSales,
            totals.NayaxReportedGrossCardSales, grossDifference, Math.Abs(tolerance),
            IsReconciled(grossDifference, tolerance), quality,
            totals.CardTransactionCount, totals.NayaxReportedCardTransactionCount, totals.CountDifference,
            totals.ProcessingFeesExGst, actualNet, first.PayoutDate)
        {
            TotalVendingSales = totals.TotalVendingSales,
            CardSales = totals.CardSales,
            CashSales = totals.CashSales,
            TotalTransactionCount = sales.Count,
            CashTransactionCount = paymentSummary.CashTransactions,
            PendingTransactionCount = statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
            RefundedTransactionCount = statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
            DeclinedOrCancelledTransactionCount = statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
            UnknownStatusTransactionCount = statusSales.Count(x => x.TransactionStatusId is not null && NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown),
            CardTransactionSales = totals.CardTransactionSales,
            NayaxReportedGrossCardSales = totals.NayaxReportedGrossCardSales,
            NayaxReportedCardTransactionCount = totals.NayaxReportedCardTransactionCount,
            GrossDifference = totals.GrossDifference,
            GrossStatus = totals.GrossStatus,
            ProcessingFeesExGst = totals.ProcessingFeesExGst,
            FeeGst = totals.FeeGst,
            OtherFees = totals.OtherFees,
            Adjustments = totals.Adjustments,
            ExpectedNetReimbursement = totals.ExpectedNetReimbursement,
            ActualNetReimbursement = actualNet,
            SettlementDifference = totals.SettlementDifference,
            SettlementStatus = totals.SettlementStatus,
            Status = totals.Status,
            PeriodRows = periodRows,
            Totals = totals
        };
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
                CardSales = g.Where(x => x.PaymentMethod == "Credit Card" || x.PaymentMethod == "Prepaid Credit").Sum(x => x.SettlementValue),
                CashSales = g.Where(x => x.PaymentMethod == "Cash").Sum(x => x.SettlementValue),
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
                commissions.GetValueOrDefault(x.MachineID).Percent,
                x.CardSales, x.CashSales)).ToList(),
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
                Transactions = g.Count(),
                CardRevenue = g.Where(x => x.PaymentMethod == "Credit Card" || x.PaymentMethod == "Prepaid Credit").Sum(x => x.SettlementValue),
                CashRevenue = g.Where(x => x.PaymentMethod == "Cash").Sum(x => x.SettlementValue)
            }).OrderByDescending(x => x.Sales).ToListAsync(cancellationToken);

        var result = rows.Select(x =>
        {
            products.TryGetValue(x.NayaxProductId ?? 0, out var product);
            var cost = product is null ? 0m : product.UnitPrice * x.Quantity;
            var name = product?.Name ?? (string.IsNullOrWhiteSpace(x.ProductName) ? "Unmapped product" : x.ProductName);
            return new ProductProfitabilityRowDto(x.NayaxProductId, name, product?.Category?.Name,
                x.Sales, x.Quantity, cost, ReportingCalculations.GrossProfit(x.Sales, cost), ReportingCalculations.MarginPercent(x.Sales, cost),
                x.Transactions, product is null, product is not null, x.CardRevenue, x.CashRevenue);
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
        var paymentSummary = await GetPaymentSummaryAsync(range, MachineId(filter), cancellationToken);
        var imported = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, MachineId(filter), cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(range, MachineId(filter), commissions, cancellationToken);
        var receiptCosts = await ReceiptCostsAsync(range, cancellationToken);
        var fees = imported.FeesIncludingGst;
        var totalSales = paymentSummary.GrossSales;
        var costOfGoods = summary?.Cost ?? 0m;
        var grossProfit = ReportingCalculations.GrossProfit(totalSales, costOfGoods);
        var otherOperatingExpenses = receiptCosts.Total;
        var netProfit = grossProfit - fees - siteCommission - otherOperatingExpenses;
        var expectedReimbursement = paymentSummary.CardSales - fees;
        var actualReimbursement = imported.NetSettlement;
        var reimbursementDifference = actualReimbursement - expectedReimbursement;
        var reconciliationStatus = !imported.ContainsRows
            ? "Pending"
            : Math.Abs(reimbursementDifference) <= 0.01m ? "Reconciled" : "Needs Review";
        return new DashboardReportDto(range.From, range.ToDate, summary?.Sales ?? 0m,
            grossProfit, summary?.Transactions ?? 0,
            summary?.Quantity ?? 0m, summary?.Machines ?? 0, summary?.Products ?? 0,
            productReport.Rows.Count(x => x.IsUnmapped), productReport.DataQuality, fees,
            actualReimbursement,
            siteCommission, netProfit,
            ReportingCalculations.MarginPercent(totalSales, costOfGoods + fees + siteCommission + otherOperatingExpenses),
            imported.FeesExGst, receiptCosts.Delivery, receiptCosts.Package, receiptCosts.Total,
            paymentSummary.CardSales, paymentSummary.CashSales, paymentSummary.CardTransactions, paymentSummary.CashTransactions)
        {
            TotalSales = totalSales,
            CostOfGoodsSold = costOfGoods,
            AverageSale = ReportingCalculations.Average(totalSales, summary?.Transactions ?? 0),
            GrossMarginPercent = ReportingCalculations.MarginPercent(totalSales, grossProfit),
            NayaxFeesIncludingGst = fees,
            OtherOperatingExpenses = otherOperatingExpenses,
            ExpectedReimbursement = expectedReimbursement,
            ActualReimbursement = actualReimbursement,
            ReimbursementDifference = reimbursementDifference,
            IsReconciled = imported.ContainsRows && Math.Abs(reimbursementDifference) <= 0.01m,
            ReconciliationStatus = reconciliationStatus,
            ReconciliationTolerance = 0.01m,
            AdjustmentsSupported = false
        };
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
            var rows = new[] { new List<string>
                {
                    "Date", "GrossSales", "CardSales", "CashSales", "AverageSale", "Quantity",
                    "CostOfGoods", "GrossProfit", "GrossMarginPercent", "Transactions",
                    "IsCogsComplete", "UncostedTransactionCount", "UncostedSalesAmount",
                    "NayaxFeesExGst", "NayaxFeesIncludingGst", "ImportedReimbursement",
                    "NetReimbursement", "ReconciliationStatus"
                } }
                .Concat(value.Rows.Select(x => new List<string>
                {
                    x.Date.ToString("yyyy-MM-dd"), x.GrossSales.ToString(CultureInfo.InvariantCulture),
                    x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture),
                    x.AverageSale.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture),
                    x.CostOfGoods.ToString(CultureInfo.InvariantCulture), x.GrossProfit.ToString(CultureInfo.InvariantCulture),
                    x.GrossMarginPercent.ToString(CultureInfo.InvariantCulture), x.TransactionCount.ToString(),
                    x.IsCogsComplete.ToString(), x.UncostedTransactionCount.ToString(),
                    x.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    x.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), x.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture),
                    x.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), x.NetReimbursement.ToString(CultureInfo.InvariantCulture),
                    x.ReconciliationStatus
                })).ToList();
            if (value.Totals is not null)
                rows.Add(new List<string>
                {
                    "TOTAL", value.Totals.GrossSales.ToString(CultureInfo.InvariantCulture),
                    value.Totals.CardSales.ToString(CultureInfo.InvariantCulture), value.Totals.CashSales.ToString(CultureInfo.InvariantCulture),
                    value.Totals.AverageSale.ToString(CultureInfo.InvariantCulture), value.Totals.Quantity.ToString(CultureInfo.InvariantCulture),
                    value.Totals.CostOfGoods.ToString(CultureInfo.InvariantCulture), value.Totals.GrossProfit.ToString(CultureInfo.InvariantCulture),
                    value.Totals.GrossMarginPercent.ToString(CultureInfo.InvariantCulture), value.Totals.TransactionCount.ToString(),
                    value.Totals.IsCogsComplete.ToString(), value.Totals.UncostedTransactionCount.ToString(),
                    value.Totals.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    value.Totals.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), value.Totals.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture),
                    value.Totals.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), value.Totals.NetReimbursement.ToString(CultureInfo.InvariantCulture),
                    string.Empty
                });
            return rows;
        }
        if (report is "bookkeeping")
        {
            var value = await GetBookkeepingAsync(filter, cancellationToken);
            return new List<List<string>>
            {
                new() { "From", "To", "FinancialYear", "GrossSales", "CardSales", "CashSales", "CardTransactions", "CashTransactions", "COGS", "GrossProfit", "NayaxFeesExGst", "NayaxFeesIncludingGst", "NayaxProcessingRate", "DeliveryCosts", "PackageCosts", "OtherOperatingExpenses", "NetSettlement", "SiteCommission", "NetProfit", "NetMargin", "GstOnSales", "GstOnFees" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.FinancialYear, value.Sales.ToString(CultureInfo.InvariantCulture), value.CardSales.ToString(CultureInfo.InvariantCulture), value.CashSales.ToString(CultureInfo.InvariantCulture), value.CardTransactionCount.ToString(), value.CashTransactionCount.ToString(), value.CostOfGoods.ToString(CultureInfo.InvariantCulture), value.GrossProfit.ToString(CultureInfo.InvariantCulture), value.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), value.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingRate.ToString(CultureInfo.InvariantCulture), value.DeliveryCosts.ToString(CultureInfo.InvariantCulture), value.PackageCosts.ToString(CultureInfo.InvariantCulture), value.OtherOperatingExpenses.ToString(CultureInfo.InvariantCulture), value.NetSettlement.ToString(CultureInfo.InvariantCulture), value.SiteCommission.ToString(CultureInfo.InvariantCulture), value.NetProfit.ToString(CultureInfo.InvariantCulture), value.NetMarginPercent.ToString(CultureInfo.InvariantCulture), value.GstOnSales.ToString(CultureInfo.InvariantCulture), value.GstOnFees.ToString(CultureInfo.InvariantCulture) }
            };
        }
        if (report is "reconciliation")
        {
            var value = await GetReconciliationAsync(filter, cancellationToken: cancellationToken);
            var rows = new List<List<string>>
            {
                new() { "From", "To", "TotalVendingSales", "CardSales", "CashSales", "CardTransactionSales", "NayaxReportedGrossCardSales", "CardTransactionCount", "NayaxReportedCardTransactionCount", "CountDifference", "GrossDifference", "GrossStatus", "ProcessingFeesExGst", "FeeGst", "OtherFees", "Adjustments", "ExpectedNetReimbursement", "ActualNetReimbursement", "SettlementDifference", "SettlementStatus", "Status", "PayoutDate" }
            };
            rows.AddRange(value.PeriodRows.Select(x => new List<string>
            {
                x.From.ToString("yyyy-MM-dd"), x.To.ToString("yyyy-MM-dd"),
                x.TotalVendingSales.ToString(CultureInfo.InvariantCulture), x.CardSales.ToString(CultureInfo.InvariantCulture),
                x.CashSales.ToString(CultureInfo.InvariantCulture), x.CardTransactionSales.ToString(CultureInfo.InvariantCulture),
                x.NayaxReportedGrossCardSales.ToString(CultureInfo.InvariantCulture), x.CardTransactionCount.ToString(),
                x.NayaxReportedCardTransactionCount.ToString(), x.CountDifference.ToString(),
                x.GrossDifference.ToString(CultureInfo.InvariantCulture), x.GrossStatus,
                x.ProcessingFeesExGst.ToString(CultureInfo.InvariantCulture), x.FeeGst.ToString(CultureInfo.InvariantCulture),
                x.OtherFees.ToString(CultureInfo.InvariantCulture), x.Adjustments.ToString(CultureInfo.InvariantCulture),
                x.ExpectedNetReimbursement.ToString(CultureInfo.InvariantCulture), x.ActualNetReimbursement.ToString(CultureInfo.InvariantCulture),
                x.SettlementDifference.ToString(CultureInfo.InvariantCulture), x.SettlementStatus, x.Status,
                x.PayoutDate?.ToString("yyyy-MM-dd") ?? string.Empty
            }));
            if (value.Totals is not null)
            {
                var x = value.Totals;
                rows.Add(new List<string>
                {
                    "TOTAL", string.Empty, x.TotalVendingSales.ToString(CultureInfo.InvariantCulture),
                    x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture),
                    x.CardTransactionSales.ToString(CultureInfo.InvariantCulture), x.NayaxReportedGrossCardSales.ToString(CultureInfo.InvariantCulture),
                    x.CardTransactionCount.ToString(), x.NayaxReportedCardTransactionCount.ToString(), x.CountDifference.ToString(),
                    x.GrossDifference.ToString(CultureInfo.InvariantCulture), x.GrossStatus,
                    x.ProcessingFeesExGst.ToString(CultureInfo.InvariantCulture), x.FeeGst.ToString(CultureInfo.InvariantCulture),
                    x.OtherFees.ToString(CultureInfo.InvariantCulture), x.Adjustments.ToString(CultureInfo.InvariantCulture),
                    x.ExpectedNetReimbursement.ToString(CultureInfo.InvariantCulture), x.ActualNetReimbursement.ToString(CultureInfo.InvariantCulture),
                    x.SettlementDifference.ToString(CultureInfo.InvariantCulture), x.SettlementStatus, x.Status, string.Empty
                });
            }
            return rows;
        }
        if (report is "machine-profitability" or "machines")
        {
            var value = await GetMachineProfitabilityAsync(filter, cancellationToken);
            return new[] { new List<string> { "MachineId", "MachineName", "Sales", "CardSales", "CashSales", "Quantity", "CostOfGoods", "GrossProfit", "CommissionPercent", "SiteCommission", "NetProfit", "NetMarginPercent", "Transactions" } }
                .Concat(value.Rows.Select(x => new List<string> { x.MachineId.ToString(), x.MachineName, x.Sales.ToString(CultureInfo.InvariantCulture), x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), x.CostOfGoods.ToString(CultureInfo.InvariantCulture), x.GrossProfit.ToString(CultureInfo.InvariantCulture), x.CommissionPercent.ToString(CultureInfo.InvariantCulture), x.SiteCommission.ToString(CultureInfo.InvariantCulture), x.NetProfit.ToString(CultureInfo.InvariantCulture), x.NetMarginPercent.ToString(CultureInfo.InvariantCulture), x.TransactionCount.ToString(CultureInfo.InvariantCulture) })).ToList();
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
        AllSalesQuery(range, machineId).Where(NayaxTransactionStatusClassifier.CompletedSalePredicate);

    private IQueryable<NayaxSales> AllSalesQuery(DateRange range, long? machineId) =>
        _db.NayaxSales.AsNoTracking().Where(x => x.MachineAuthorizationTime >= range.From && x.MachineAuthorizationTime < range.EndExclusive &&
            (!machineId.HasValue || x.MachineID == machineId.Value));

    private IQueryable<SaleCost> CostQuery(DateRange range, long? machineId) =>
        from sale in SalesQuery(range, machineId)
        join product in _db.Products.AsNoTracking() on sale.NayaxProductId equals product.Id into productJoin
        from product in productJoin.DefaultIfEmpty()
        select new SaleCost
        {
            MachineID = sale.MachineID, MachineName = sale.MachineName, NayaxProductId = sale.NayaxProductId,
            ProductName = sale.ProductName, PaymentMethod = sale.PaymentMethod, SettlementValue = sale.SettlementValue, Quantity = sale.Quantity,
            MachineAuthorizationTime = sale.MachineAuthorizationTime, Cost = product == null ? 0m : product.UnitPrice * sale.Quantity,
            HasCost = product != null
        };

    private async Task<SalesPaymentSummary> GetPaymentSummaryAsync(DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        var rows = await SalesQuery(range, machineId)
            .Select(x => new { x.PaymentMethod, x.SettlementValue })
            .ToListAsync(cancellationToken);
        var classified = rows.GroupBy(x => PaymentMethodClassifier.Classify(x.PaymentMethod))
            .ToDictionary(x => x.Key, x => new { Sales = x.Sum(y => y.SettlementValue), Count = x.Count() });
        var card = classified.GetValueOrDefault(NayaxPaymentType.Card);
        var cash = classified.GetValueOrDefault(NayaxPaymentType.Cash);
        var unknown = classified.GetValueOrDefault(NayaxPaymentType.Unknown);
        return new SalesPaymentSummary(
            rows.Sum(x => x.SettlementValue),
            rows.Count,
            card?.Sales ?? 0m,
            card?.Count ?? 0,
            cash?.Sales ?? 0m,
            cash?.Count ?? 0,
            unknown?.Sales ?? 0m,
            unknown?.Count ?? 0);
    }

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

    private async Task<Dictionary<DateTime, DailyImportedSummary>> DailyImportedSummaryAsync(
        DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = await _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate.HasValue && x.ReimbursementEndDate.HasValue &&
                x.ReimbursementStartDate < range.EndExclusive && x.ReimbursementEndDate >= range.From)
            .Select(x => new
            {
                x.Id,
                Start = x.ReimbursementStartDate!.Value,
                End = x.ReimbursementEndDate!.Value,
                x.Total
            })
            .ToListAsync(cancellationToken);
        if (reimbursements.Count == 0)
            return new();

        var ids = reimbursements.Select(x => x.Id).ToList();
        var fees = await _db.ImportedFees.AsNoTracking()
            .Where(x => ids.Contains(x.ImportedReimbursementId) && !x.IsPreviousPeriod)
            .Select(x => new
            {
                x.ImportedReimbursementId,
                x.TotalSum,
                x.TotalSumWithVat,
                x.VatPercentage
            })
            .ToListAsync(cancellationToken);
        var devices = machineId.HasValue
            ? await _db.ImportedReimbursementDevices.AsNoTracking()
                .Where(x => ids.Contains(x.ImportedReimbursementId))
                .Select(x => new { x.ImportedReimbursementId, x.MachineNumber, x.TotalBillableTransactionAmount })
                .ToListAsync(cancellationToken)
            : new();

        var result = new Dictionary<DateTime, DailyImportedSummary>();
        foreach (var reimbursement in reimbursements)
        {
            var start = reimbursement.Start.Date;
            var end = reimbursement.End.Date;
            var matchingMachine = machineId.HasValue && devices.Any(x =>
                x.ImportedReimbursementId == reimbursement.Id &&
                long.TryParse(x.MachineNumber, out var parsed) && parsed == machineId.Value);
            if (machineId.HasValue && !matchingMachine)
                continue;

            var daily = start == end && start >= range.From && start <= range.ToDate;
            if (!daily)
            {
                if (start <= range.ToDate && end >= range.From)
                {
                    var periodStart = start < range.From.Date ? range.From.Date : start;
                    var periodEnd = end > range.ToDate.Date ? range.ToDate.Date : end;
                    for (var date = periodStart; date <= periodEnd; date = date.AddDays(1))
                    {
                        var existingPeriod = result.GetValueOrDefault(date);
                        result[date] = existingPeriod with { HasPeriodOnlyData = true };
                    }
                }
                continue;
            }

            var reimbursementFees = fees.Where(x => x.ImportedReimbursementId == reimbursement.Id).ToList();
            var reimbursementAmount = machineId.HasValue
                ? devices.Where(x => x.ImportedReimbursementId == reimbursement.Id &&
                    long.TryParse(x.MachineNumber, out var parsed) && parsed == machineId.Value)
                    .Sum(x => x.TotalBillableTransactionAmount ?? 0m)
                : reimbursement.Total ?? 0m;
            var summary = new DailyImportedSummary(
                reimbursementAmount,
                machineId.HasValue ? 0m : reimbursementFees.Sum(x => x.TotalSum ?? 0m),
                machineId.HasValue ? 0m : reimbursementFees.Sum(x => x.TotalSumWithVat ?? x.TotalSum ?? 0m),
                reimbursementFees.Any(x => x.VatPercentage.HasValue),
                true,
                reimbursement.Total ?? 0m,
                machineId.HasValue);
            result[start] = result.GetValueOrDefault(start) + summary;
        }
        return result;
    }

    private async Task<ImportedSummary> ImportedSummaryAsync(DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        var reimbursements = _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate < range.EndExclusive && x.ReimbursementEndDate >= range.From);
        var reimbursementRows = await reimbursements
            .Select(x => new ImportedReimbursementRow
            {
                Id = x.Id,
                Total = x.Total,
                PayoutDate = x.ReimbursementPayoutDate
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
                Fees = (x.ProcessingFees ?? 0m) + (x.ServiceFees ?? 0m),
                Count = x.SalesCount ?? 1
            })
            .ToListAsync(cancellationToken);

        var rows = reimbursementRows.Select(x => new ImportedReportRow
        {
            Id = x.Id,
            Settlement = x.Total ?? 0m,
            NetSettlement = x.Total ?? 0m,
            HasImportedTotal = x.Total.HasValue,
            PayoutDate = x.PayoutDate,
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
                    .Select(p => new ImportedPaymentNet(p.PaymentMethodDescription, p.RecognitionDescription, p.Amount, p.Fees, p.Count))
                    .ToList()
            }).ToList()
        }).ToList();

        var importedCardTransactionCount = rows.SelectMany(x => x.Devices)
            .SelectMany(d => d.Payments)
            .Where(p => PaymentMethodClassifier.Classify(p.PaymentMethodDescription, p.RecognitionDescription) == NayaxPaymentType.Card)
            .Sum(p => p.Count);

        if (!machineId.HasValue)
            return new ImportedSummary(rows.Sum(x => x.Settlement), rows.Sum(x => x.FeesExGst),
                rows.Sum(x => x.FeesIncludingGst), rows.Sum(x => x.Gst),
                rows.Sum(x => x.NetSettlement),
                rows.Count != 0, rows.Any(x => x.HasGst), rows.Any(x => x.HasImportedTotal), rows.Count != 0, rows.Select(x => x.PayoutDate).FirstOrDefault(), importedCardTransactionCount);

        var matchingDevices = rows.SelectMany(x => x.Devices)
            .Where(d => long.TryParse(d.MachineNumber, out var parsed) && parsed == machineId.Value)
            .ToList();
        var matchingReimbursementIds = rows
            .Where(row => row.Devices.Any(device => long.TryParse(device.MachineNumber, out var parsed) && parsed == machineId.Value))
            .Select(row => row.Id)
            .ToHashSet();
        var matchingRows = rows.Where(row => matchingReimbursementIds.Contains(row.Id)).ToList();
        var matchingSettlement = matchingDevices
            .Sum(d => d.Payments.Count != 0
                ? d.Payments.Where(p => PaymentMethodClassifier.Classify(p.PaymentMethodDescription, p.RecognitionDescription) == NayaxPaymentType.Card).Sum(p => p.Amount)
                : d.Gross);
        return new ImportedSummary(matchingSettlement, 0m,
            0m, 0m,
            matchingRows.Sum(x => x.NetSettlement),
            rows.Count != 0, matchingRows.Any(x => x.HasGst), matchingRows.Any(x => x.HasImportedTotal), matchingDevices.Count != 0,
            matchingRows.Select(x => x.PayoutDate).FirstOrDefault(),
            matchingDevices.SelectMany(d => d.Payments)
                .Where(p => PaymentMethodClassifier.Classify(p.PaymentMethodDescription, p.RecognitionDescription) == NayaxPaymentType.Card)
                .Sum(p => p.Count),
            true);
    }

    private ReconciliationPeriodDto BuildReconciliationPeriod(
        ImportedReimbursement? reimbursement,
        IReadOnlyList<NayaxSales> sales,
        long? machineId,
        decimal tolerance,
        DateTime? fallbackFrom = null,
        DateTime? fallbackTo = null)
    {
        var totalVendingSales = sales.Sum(x => x.SettlementValue);
        var cardSales = sales.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card)
            .Sum(x => x.SettlementValue);
        var cashSales = sales.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash)
            .Sum(x => x.SettlementValue);
        var cardTransactionCount = sales.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card);
        var hasImported = reimbursement is not null;
        var devices = reimbursement?.Devices
            .Where(x => !machineId.HasValue ||
                (long.TryParse(x.MachineNumber, out var parsed) && parsed == machineId.Value))
            .ToList() ?? new List<ImportedReimbursementDevice>();
        var paymentRows = devices.Count == 0
            ? (!machineId.HasValue ? reimbursement?.DevicePayments.ToList() ?? new List<ImportedDevicePayment>() : new List<ImportedDevicePayment>())
            : reimbursement!.DevicePayments.Where(x =>
                devices.Any(d => d.EntityId is not null && d.EntityId == x.EntityId) ||
                (devices.Count == 1 && devices[0].EntityId is null && x.EntityId is null)).ToList();
        var hasPaymentRows = paymentRows.Count != 0;
        var paymentMethodRows = !machineId.HasValue
            ? reimbursement?.PaymentMethods.Where(x => !x.IsPreviousPeriod).ToList() ?? new List<ImportedPaymentMethod>()
            : new List<ImportedPaymentMethod>();
        var hasAccountPaymentRows = paymentMethodRows.Count != 0;
        var reportedGross = hasPaymentRows
            ? paymentRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                .Sum(x => x.TotalSum ?? 0m)
            : hasAccountPaymentRows
                ? paymentMethodRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                    .Sum(x => x.TotalSalesSum ?? 0m)
            : devices.Count != 0
                ? devices.Sum(x => x.TotalBillableTransactionAmount ?? 0m)
                : machineId.HasValue ? 0m : reimbursement?.Total ?? 0m;
        var reportedCount = hasPaymentRows
            ? paymentRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                .Sum(x => x.SalesCount ?? 1)
            : hasAccountPaymentRows
                ? paymentMethodRows.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethodDescription, x.RecognitionDescription) == NayaxPaymentType.Card)
                    .Sum(x => x.TotalSalesCount ?? 1)
            : devices.Sum(x => x.TotalBillableTransactionCount ?? 0);

        var fees = reimbursement?.Fees.Where(x => !x.IsPreviousPeriod).ToList() ?? new List<ImportedFee>();
        var processingFees = fees.Where(IsProcessingFee).Sum(FeeExGst);
        var otherFees = fees.Where(x => !IsProcessingFee(x)).Sum(FeeExGst);
        var feeGst = fees.Sum(FeeGst);
        var adjustments = 0m;
        var expectedNet = reportedGross - processingFees - otherFees - feeGst - adjustments;
        var actualNet = reimbursement?.Total ?? 0m;
        var grossDifference = cardSales - reportedGross;
        var settlementDifference = expectedNet - actualNet;
        var paymentDetailMissing = hasImported && !hasPaymentRows && !hasAccountPaymentRows;
        var warning = machineId.HasValue || paymentDetailMissing ||
            sales.Any(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Unknown);
        var grossStatus = StatusFor(!hasImported, grossDifference, tolerance, warning);
        var settlementStatus = StatusFor(!hasImported, settlementDifference, tolerance, warning);
        var notes = new List<string>();
        if (!hasImported)
            notes.Add("No imported reimbursement row matched the requested start and end dates.");
        if (machineId.HasValue)
            notes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        if (paymentDetailMissing)
            notes.Add("Imported card payment detail was unavailable; device or reimbursement gross was used as the card gross.");
        if (adjustments == 0m)
            notes.Add("Adjustments are unsupported by the imported reimbursement model and are treated as zero.");
        var quality = Quality(hasImported, fees.Any(x => x.VatPercentage.HasValue), false,
            string.Join(" ", notes));
        return new ReconciliationPeriodDto(
            reimbursement?.ReimbursementStartDate?.Date ?? fallbackFrom!.Value.Date,
            reimbursement?.ReimbursementEndDate?.Date ?? fallbackTo!.Value.Date,
            totalVendingSales, cardSales, cashSales, cardSales, reportedGross,
            cardTransactionCount, reportedCount, cardTransactionCount - reportedCount,
            grossDifference, grossStatus, processingFees, feeGst, otherFees, adjustments,
            expectedNet, actualNet, settlementDifference, settlementStatus,
            OverallStatus(grossStatus, settlementStatus), reimbursement?.ReimbursementPayoutDate, quality)
        {
            TotalTransactionCount = sales.Count,
            CashTransactionCount = sales.Count(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash)
        };
    }

    private static bool IsProcessingFee(ImportedFee fee) =>
        (fee.FeesTypeId ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase) ||
        (fee.FeeTypeDescription ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase);

    private static decimal FeeGst(ImportedFee fee)
    {
        if (fee.TotalSumWithVat.HasValue && fee.TotalSum.HasValue)
            return fee.TotalSumWithVat.Value - fee.TotalSum.Value;
        if (fee.TotalSumWithVat.HasValue && fee.VatPercentage.HasValue)
            return fee.TotalSumWithVat.Value * fee.VatPercentage.Value / (100m + fee.VatPercentage.Value);
        return 0m;
    }

    private static decimal FeeExGst(ImportedFee fee) =>
        fee.TotalSum ?? (fee.TotalSumWithVat.HasValue ? fee.TotalSumWithVat.Value - FeeGst(fee) : 0m);

    private static bool IsReconciled(decimal difference, decimal tolerance) =>
        Math.Abs(difference) <= Math.Abs(tolerance);

    private static string StatusFor(bool pending, decimal difference, decimal tolerance, bool warning) =>
        pending ? "Pending" : !IsReconciled(difference, tolerance) ? "Mismatch" : warning ? "Warning" : "Reconciled";

    private static string StatusFor(bool pending, bool mismatch, bool warning) =>
        pending ? "Pending" : mismatch ? "Mismatch" : warning ? "Warning" : "Reconciled";

    private static string OverallStatus(string grossStatus, string settlementStatus) =>
        grossStatus == "Mismatch" || settlementStatus == "Mismatch" ? "Mismatch" :
        grossStatus == "Pending" || settlementStatus == "Pending" ? "Pending" :
        grossStatus == "Warning" || settlementStatus == "Warning" ? "Warning" : "Reconciled";

    private static string ReconciliationStatus(bool hasImported, decimal difference, decimal tolerance, bool hasWarning = false, bool hasPeriodOnlyData = false) =>
        StatusFor(!hasImported && !hasPeriodOnlyData, difference, tolerance, hasWarning || hasPeriodOnlyData);

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

    private static void AddStatusQualityNotes(List<string> notes, IEnumerable<NayaxSales> sales)
    {
        var pending = sales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending);
        var refunded = sales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded);
        var declined = sales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined);
        var unknown = sales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown);
        var unknownStatus = sales.Count(x => x.TransactionStatusId is null);
        if (pending > 0) notes.Add($"{pending} pending Nayax transaction(s) are excluded from completed sales.");
        if (refunded > 0) notes.Add($"{refunded} refunded Nayax transaction(s) are excluded from completed sales.");
        if (declined > 0) notes.Add($"{declined} cancelled or declined Nayax transaction(s) are excluded from completed sales.");
        if (unknown > 0) notes.Add($"{unknown} Nayax transaction(s) have unrecognised status IDs.");
        if (unknownStatus > 0) notes.Add($"{unknownStatus} Nayax transaction(s) have no status ID and are excluded from completed sales.");
    }

    private static ReportingDataQualityDto Quality(bool importedRows, bool gstClassification, bool unmapped, string? note = null) =>
        new(false, true, true, true, unmapped,
            new[] { "Nayax status IDs are stored raw; existing historical rows were backfilled to status 12 by migration; rows still missing a status are excluded.", "Historical product cost is represented by the current Product.UnitPrice.", "Commission is read from Nayax machine products; the first product with a commission defines the machine rate.", "GST classification is not persisted on sales; GST amounts are an indicative 10% inclusive calculation." }
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
        public string? PaymentMethod { get; set; }
        public decimal SettlementValue { get; set; }
        public decimal Quantity { get; set; }
        public DateTime MachineAuthorizationTime { get; set; }
        public decimal Cost { get; set; }
        public bool HasCost { get; set; }
    }

    private sealed class ImportedReimbursementRow
    {
        public int Id { get; set; }
        public decimal? Total { get; set; }
        public DateTime? PayoutDate { get; set; }
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
        public int Count { get; set; }
    }

    private sealed class ImportedReportRow
    {
        public int Id { get; set; }
        public decimal Settlement { get; set; }
        public decimal NetSettlement { get; set; }
        public bool HasImportedTotal { get; set; }
        public decimal FeesExGst { get; set; }
        public decimal FeesIncludingGst { get; set; }
        public decimal Gst { get; set; }
        public DateTime? PayoutDate { get; set; }
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
    private readonly record struct SalesPaymentSummary(
        decimal GrossSales,
        int Transactions,
        decimal CardSales,
        int CardTransactions,
        decimal CashSales,
        int CashTransactions,
        decimal UnknownSales,
        int UnknownTransactions);
    private readonly record struct ReceiptCostSummary(decimal Delivery, decimal Package)
    {
        public decimal Total => Delivery + Package;
    }

    private readonly record struct ImportedPaymentNet(
        string? PaymentMethodDescription,
        string? RecognitionDescription,
        decimal Amount,
        decimal Fees,
        int Count = 1);

    private readonly record struct DailyImportedSummary(
        decimal Reimbursement,
        decimal FeesExGst,
        decimal FeesIncludingGst,
        bool HasGstClassification,
        bool HasReimbursement,
        decimal NetReimbursement,
        bool FeesMachineFilterLimited,
        bool HasPeriodOnlyData = false)
    {
        public static DailyImportedSummary operator +(DailyImportedSummary left, DailyImportedSummary right) =>
            new(left.Reimbursement + right.Reimbursement,
                left.FeesExGst + right.FeesExGst,
                left.FeesIncludingGst + right.FeesIncludingGst,
                left.HasGstClassification || right.HasGstClassification,
                left.HasReimbursement || right.HasReimbursement,
                left.NetReimbursement + right.NetReimbursement,
                left.FeesMachineFilterLimited || right.FeesMachineFilterLimited,
                left.HasPeriodOnlyData || right.HasPeriodOnlyData);
    }

    private readonly record struct ImportedSummary(
        decimal Settlement,
        decimal FeesExGst,
        decimal FeesIncludingGst,
        decimal GstOnFees,
        decimal NetSettlement,
        bool ContainsRows,
        bool ContainsGstClassification,
        bool HasNetSettlement,
        bool MachineFilterMatched,
        DateTime? PayoutDate = null,
        int CardTransactionCount = 0,
        bool FeesMachineFilterLimited = false);
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
    public static decimal PercentageOf(decimal amount, decimal denominator) =>
        denominator == 0m ? 0m : amount / denominator * 100m;
    public static decimal GstFromInclusive(decimal amount) => amount * 10m / 110m;
    public static decimal Average(decimal amount, int count) => count == 0 ? 0m : amount / count;
}
