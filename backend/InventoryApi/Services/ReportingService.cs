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
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;
    private readonly ISiteCommissionService _siteCommissions;

    public ReportingService(AppDbContext db, INayaxProcessingFeeService nayaxProcessingFees,
        ISiteCommissionService siteCommissions, INayaxLynxClient? nayaxLynxClient = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _nayaxProcessingFees = nayaxProcessingFees;
        _siteCommissions = siteCommissions;
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
        var machineId = MachineId(filter);
        var isMachineFiltered = machineId.HasValue;
        var paymentSummary = await GetPaymentSummaryAsync(range, machineId, cancellationToken);
        var sales = paymentSummary.GrossSales;

        var saleCosts = await CostQuery(range, machineId).ToListAsync(cancellationToken);
        var partialCost = saleCosts.Sum(x => x.CostOfGoodsSold ?? 0m);
        var uncosted = saleCosts.Where(x => !x.HasCost).ToList();
        var isCogsComplete = uncosted.Count == 0;
        var receiptCosts = isMachineFiltered ? new ReceiptCostSummary(0m, 0m) : await ReceiptCostsAsync(range, cancellationToken);
        var imported = await ImportedSummaryAsync(range, machineId, cancellationToken);
        var processingFees = await _nayaxProcessingFees.GetProcessingFeesAsync(range.From, range.ToDate, machineId, cancellationToken);
        var operatingExpenses = await OperatingExpenseSummaryAsync(range, machineId, cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, machineId, cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(range, machineId, commissions, cancellationToken);
        var fees = processingFees.TotalFeeExGst;
        var feesIncludingGst = processingFees.TotalFeeIncGst;
        var netSettlement = imported.HasNetSettlement ? imported.NetSettlement : paymentSummary.CardSales - feesIncludingGst;
        decimal? grossProfit = isCogsComplete ? ReportingCalculations.GrossProfit(sales, partialCost) : null;
        var commissionCompleteForScope = isMachineFiltered
            ? commissions.Machines.GetValueOrDefault(machineId!.Value).IsComplete
            : commissions.IsComplete;
        var isProfitComplete = grossProfit.HasValue && !processingFees.HasMissingRates && commissionCompleteForScope;
        decimal? directProfit = isMachineFiltered && isProfitComplete
            ? grossProfit!.Value - feesIncludingGst - siteCommission - operatingExpenses.Total
            : null;
        decimal? netProfit = !isMachineFiltered && isProfitComplete
            ? grossProfit.Value - feesIncludingGst - siteCommission - receiptCosts.Total - operatingExpenses.Total
            : null;
        var gstOnFees = processingFees.TotalFeeGst;
        var qualityNotes = new List<string>();
        if (paymentSummary.UnknownTransactions > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        if (isMachineFiltered && !imported.MachineFilterMatched)
            qualityNotes.Add("No reimbursement device row matched the selected machine ID.");
        if (imported.FeesMachineFilterLimited)
            qualityNotes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        if (processingFees.HasMissingRates)
            qualityNotes.Add($"{processingFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; profit is unavailable.");
        if (!isCogsComplete)
            qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");
        if (isMachineFiltered)
            qualityNotes.Add("Net profit is unavailable for a machine-filtered report because shared business overhead is not allocated to individual machines.");
        AddCommissionQualityNotes(qualityNotes, commissions, isMachineFiltered ? "direct profit" : "net profit");
        var quality = Quality(imported.ContainsRows, imported.ContainsGstClassification, false,
            qualityNotes.Count == 0 ? null : string.Join(" ", qualityNotes));
        return new BookkeepingReportDto(range.From, range.ToDate, AustralianFyHelper.Label(range.From),
            sales, isCogsComplete ? partialCost : null, grossProfit, fees, netSettlement,
            ReportingCalculations.GstFromInclusive(sales), gstOnFees, quality, siteCommission, netProfit,
            netProfit.HasValue ? ReportingCalculations.MarginPercent(sales, partialCost + feesIncludingGst + siteCommission + receiptCosts.Total + operatingExpenses.Total) : null,
            fees, feesIncludingGst, receiptCosts.Delivery, receiptCosts.Package, operatingExpenses.Total,
            paymentSummary.CardSales, paymentSummary.CashSales, paymentSummary.CardTransactions,
            paymentSummary.CashTransactions, ReportingCalculations.PercentageOf(feesIncludingGst, paymentSummary.CardSales))
        {
            PartialCostOfGoods = partialCost,
            IsCogsComplete = isCogsComplete,
            UncostedTransactionCount = uncosted.Count,
            UncostedSalesAmount = uncosted.Sum(x => x.SettlementValue),
            StructuredOperatingExpenses = operatingExpenses.Total,
            OperatingExpenseGst = operatingExpenses.Gst,
            OperatingExpensesByCategory = operatingExpenses.ByCategory,
            DirectProfit = directProfit,
            DirectMarginPercent = directProfit.HasValue
                ? ReportingCalculations.MarginPercent(sales, partialCost + feesIncludingGst + siteCommission + operatingExpenses.Total)
                : null,
            NayaxProcessingFees = processingFees
        };
    }

    public async Task<DailyReportDto> GetDailyAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var sales = await CostQuery(range, MachineId(filter)).ToListAsync(cancellationToken);
        var statusSales = await AllSalesQuery(range, MachineId(filter)).ToListAsync(cancellationToken);
        var importedByDate = await DailyImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var importedPeriod = await ImportedSummaryAsync(range, MachineId(filter), cancellationToken);
        var feeByDate = new Dictionary<DateTime, NayaxProcessingFeeResult>();
        foreach (var date in sales.Select(x => x.MachineAuthorizationTime.Date).Distinct())
            feeByDate[date] = await _nayaxProcessingFees.GetProcessingFeesAsync(date, date, MachineId(filter), cancellationToken);
        var rows = sales
            .GroupBy(x => x.MachineAuthorizationTime.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var grossSales = g.Sum(x => x.SettlementValue);
                var partialCost = g.Sum(x => x.CostOfGoodsSold ?? 0m);
                var isCogsComplete = g.All(x => x.HasCost);
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
                var fee = feeByDate[g.Key];
                return new DailyReportRowDto(
                    g.Key, grossSales, g.Count(), isCogsComplete ? partialCost : null,
                    isCogsComplete ? ReportingCalculations.GrossProfit(grossSales, partialCost) : null, g.Count(),
                    grossSales, cardSales, cashSales,
                    ReportingCalculations.Average(grossSales, g.Count()),
                    uncosted.Count == 0, uncosted.Count, uncosted.Sum(x => x.SettlementValue),
                    isCogsComplete ? ReportingCalculations.MarginPercent(grossSales, partialCost) : null,
                    fee.TotalFeeExGst, fee.TotalFeeIncGst,
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
                    statusRows.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown),
                    fee.HasEstimatedFees ? (fee.ActualFeeExGst != 0m ? "Actual and Estimated" : "Estimated") :
                        fee.ActualFeeExGst != 0m ? "Actual" : "None")
                {
                    PartialCostOfGoods = partialCost
                };
            }).ToList();
        var totalFees = await _nayaxProcessingFees.GetProcessingFeesAsync(range.From, range.ToDate, MachineId(filter), cancellationToken);
        var totalPartialCost = sales.Sum(x => x.CostOfGoodsSold ?? 0m);
        var totalCogsComplete = sales.All(x => x.HasCost);
        var totalGrossSales = rows.Sum(x => x.GrossSales);
        var totalTransactionCount = rows.Sum(x => x.TransactionCount);
        var totals = new DailyReportTotalsDto(
            totalGrossSales, rows.Sum(x => x.CardSales), rows.Sum(x => x.CashSales),
            rows.Sum(x => x.Quantity), totalCogsComplete ? totalPartialCost : null,
            totalCogsComplete ? ReportingCalculations.GrossProfit(totalGrossSales, totalPartialCost) : null,
            totalTransactionCount, ReportingCalculations.Average(totalGrossSales, totalTransactionCount),
            totalCogsComplete, rows.Sum(x => x.UncostedTransactionCount), rows.Sum(x => x.UncostedSalesAmount),
            totalCogsComplete ? ReportingCalculations.MarginPercent(totalGrossSales, totalPartialCost) : null,
            totalFees.TotalFeeExGst,
            totalFees.TotalFeeIncGst,
            importedPeriod.ContainsRows ? importedPeriod.Settlement : rows.Sum(x => x.ImportedReimbursement),
            importedPeriod.ContainsRows ? importedPeriod.NetSettlement : rows.Sum(x => x.NetReimbursement),
            statusSales.Count(x => NayaxTransactionStatusClassifier.IsCompletedSale(x)),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Pending),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.CancelledOrDeclined),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Refunded),
            statusSales.Count(x => NayaxTransactionStatusClassifier.Classify(x.TransactionStatusId) == NayaxTransactionStatus.Unknown))
        {
            PartialCostOfGoods = totalPartialCost,
            NayaxProcessingFees = totalFees
        };
        var note = importedPeriod.FeesMachineFilterLimited
            ? "Imported fees are account-level amounts and are not allocated to a selected machine."
            : importedByDate.Values.Any(x => x.HasPeriodOnlyData)
                ? "Some reimbursements cover a period longer than one day and are not allocated to daily rows."
                : null;
        var qualityNotes = new List<string>();
        if (note is not null) qualityNotes.Add(note);
        if (totalFees.HasMissingRates)
            qualityNotes.Add($"{totalFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; fee totals are provisional.");
        if (!totals.IsCogsComplete)
            qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");
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
            hasImported && IsReconciled(grossDifference, tolerance), quality,
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
        var sales = await CostQuery(range, MachineId(filter)).ToListAsync(cancellationToken);
        var rows = sales
            .GroupBy(x => new { x.MachineID, x.MachineName })
            .Select(g => new
            {
                g.Key.MachineID,
                g.Key.MachineName,
                CardSales = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card).Sum(x => x.SettlementValue),
                CashSales = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash).Sum(x => x.SettlementValue),
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Count(),
                PartialCost = g.Sum(x => x.CostOfGoodsSold ?? 0m),
                IsCogsComplete = g.All(x => x.HasCost),
                UncostedTransactionCount = g.Count(x => !x.HasCost),
                UncostedSalesAmount = g.Where(x => !x.HasCost).Sum(x => x.SettlementValue),
                Transactions = g.Count()
            }).OrderByDescending(x => x.Sales).ToList();
        var commissions = await GetMachineCommissionsAsync(range, MachineId(filter), cancellationToken);
        var operatingExpenses = await _db.OperatingExpenses.AsNoTracking()
            .Where(x => x.ExpenseDate >= range.From && x.ExpenseDate < range.EndExclusive && x.MachineId.HasValue)
            .GroupBy(x => x.MachineId!.Value)
            .Select(g => new { MachineId = g.Key, Total = g.Sum(x => x.TotalAmount) })
            .ToDictionaryAsync(x => x.MachineId, x => x.Total, cancellationToken);
        var results = new List<MachineProfitabilityRowDto>();
        var missingFeeRateTransactions = 0;
        foreach (var x in rows)
        {
            var fees = await _nayaxProcessingFees.GetProcessingFeesAsync(range.From, range.ToDate, x.MachineID, cancellationToken);
            missingFeeRateTransactions += fees.MissingRateTransactionCount;
            var machineCommission = commissions.Machines.GetValueOrDefault(x.MachineID);
            var isDirectProfitComplete = x.IsCogsComplete && !fees.HasMissingRates && machineCommission.IsComplete;
            results.Add(new MachineProfitabilityRowDto(x.MachineID, x.MachineName ?? $"Machine {x.MachineID}",
                x.Sales, x.Quantity, x.IsCogsComplete ? x.PartialCost : null,
                x.IsCogsComplete ? ReportingCalculations.GrossProfit(x.Sales, x.PartialCost) : null,
                x.IsCogsComplete ? ReportingCalculations.MarginPercent(x.Sales, x.PartialCost) : null, x.Transactions,
                machineCommission.Due,
                isDirectProfitComplete ? x.Sales - x.PartialCost - machineCommission.Due -
                    operatingExpenses.GetValueOrDefault(x.MachineID) - fees.TotalFeeIncGst : null,
                isDirectProfitComplete ? ReportingCalculations.MarginPercent(x.Sales, x.PartialCost + machineCommission.Due +
                    operatingExpenses.GetValueOrDefault(x.MachineID) + fees.TotalFeeIncGst) : null,
                machineCommission.Percent,
                x.CardSales, x.CashSales)
            {
                PartialCostOfGoods = x.PartialCost,
                IsCogsComplete = x.IsCogsComplete,
                UncostedTransactionCount = x.UncostedTransactionCount,
                UncostedSalesAmount = x.UncostedSalesAmount,
                DirectOperatingExpenses = operatingExpenses.GetValueOrDefault(x.MachineID),
                NayaxProcessingFees = fees
            });
        }
        var qualityNotes = new List<string>();
        if (missingFeeRateTransactions > 0)
            qualityNotes.Add($"{missingFeeRateTransactions} card transaction(s) have no effective Nayax processing fee rate; machine profit is provisional.");
        if (results.Any(x => !x.IsCogsComplete))
            qualityNotes.Add("One or more machines have completed sales with no persisted COGS; profit is incomplete.");
        AddCommissionQualityNotes(qualityNotes, commissions);
        return new MachineProfitabilityReportDto(range.From, range.ToDate, results,
            Quality(false, false, false, qualityNotes.Count == 0 ? null : string.Join(" ", qualityNotes)));
    }

    public async Task<ProductProfitabilityReportDto> GetProductProfitabilityAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var products = await _db.Products.AsNoTracking().Include(p => p.Category).ToDictionaryAsync(p => p.Id, cancellationToken);
        // Payment classification is not EF-translatable, so materialize only the required columns
        // and group in memory (same pattern as machine profitability) to use PaymentMethodClassifier.
        var sales = await SalesQuery(range, MachineId(filter))
            .Select(x => new { x.NayaxProductId, x.ProductName, x.SettlementValue, x.CostOfGoodsSold, x.PaymentMethod })
            .ToListAsync(cancellationToken);
        var rows = sales
            .GroupBy(x => new { x.NayaxProductId, x.ProductName })
            .Select(g => new
            {
                g.Key.NayaxProductId,
                g.Key.ProductName,
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Count(),
                PartialCostOfGoodsSold = g.Sum(x => x.CostOfGoodsSold ?? 0m),
                IsCogsComplete = g.All(x => x.CostOfGoodsSold.HasValue),
                UncostedTransactionCount = g.Count(x => !x.CostOfGoodsSold.HasValue),
                UncostedSalesAmount = g.Where(x => !x.CostOfGoodsSold.HasValue).Sum(x => x.SettlementValue),
                Transactions = g.Count(),
                CardRevenue = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card).Sum(x => x.SettlementValue),
                CashRevenue = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash).Sum(x => x.SettlementValue)
            }).OrderByDescending(x => x.Sales).ToList();

        var result = rows
            .Select(x =>
            {
                var product = NayaxProductMatcher.Match(products.Values, x.NayaxProductId, x.ProductName);
                return new
                {
                    Product = product,
                    x.NayaxProductId,
                    UnmappedName = string.IsNullOrWhiteSpace(x.ProductName)
                        ? "Unmapped product"
                        : NayaxProductMatcher.NormalizeName(x.ProductName),
                    x.Sales,
                    x.Quantity,
                    x.PartialCostOfGoodsSold,
                    x.IsCogsComplete,
                    x.UncostedTransactionCount,
                    x.UncostedSalesAmount,
                    x.Transactions,
                    x.CardRevenue,
                    x.CashRevenue
                };
            })
            .GroupBy(x => x.Product is not null
                ? $"product:{x.Product.Id}"
                : $"unmapped:{x.NayaxProductId}:{x.UnmappedName}")
            .Select(g =>
            {
                var first = g.First();
                var sales = g.Sum(x => x.Sales);
                var partialCost = g.Sum(x => x.PartialCostOfGoodsSold);
                var isCogsComplete = g.All(x => x.IsCogsComplete);
                var isUnmapped = first.Product is null;
                return new ProductProfitabilityRowDto(
                    first.Product?.Id ?? first.NayaxProductId,
                    first.Product?.Name ?? first.UnmappedName,
                    first.Product?.Category?.Name,
                    sales,
                    g.Sum(x => x.Quantity),
                    isCogsComplete ? partialCost : null,
                    isCogsComplete ? ReportingCalculations.GrossProfit(sales, partialCost) : null,
                    isCogsComplete ? ReportingCalculations.MarginPercent(sales, partialCost) : null,
                    g.Sum(x => x.Transactions),
                    isUnmapped,
                    !isUnmapped && isCogsComplete,
                    g.Sum(x => x.CardRevenue),
                    g.Sum(x => x.CashRevenue))
                {
                    PartialCostOfGoods = partialCost,
                    IsCogsComplete = isCogsComplete,
                    UncostedTransactionCount = g.Sum(x => x.UncostedTransactionCount),
                    UncostedSalesAmount = g.Sum(x => x.UncostedSalesAmount)
                };
            })
            .OrderByDescending(x => x.Sales)
            .ToList();
        var qualityNotes = new List<string>();
        if (result.Any(x => x.IsUnmapped)) qualityNotes.Add("One or more sales could not be mapped to a Product.");
        if (result.Any(x => !x.IsCogsComplete)) qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");
        var quality = Quality(false, false, result.Any(x => x.IsUnmapped),
            qualityNotes.Count == 0 ? null : string.Join(" ", qualityNotes));
        return new ProductProfitabilityReportDto(range.From, range.ToDate, result, quality);
    }

    public async Task<GstAccountingAidDto> GetGstAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var bookkeeping = await GetBookkeepingAsync(filter, cancellationToken);
        var imported = await ImportedSummaryAsync(ResolveRange(filter), MachineId(filter), cancellationToken);
        var quality = Quality(imported.ContainsRows, imported.ContainsGstClassification, false);
        var taxableSales = bookkeeping.Sales - bookkeeping.GstOnSales;
        var taxableFees = bookkeeping.NayaxFeesExGst;
        var operatingExpenseGst = bookkeeping.OperatingExpenseGst;
        return new GstAccountingAidDto(bookkeeping.From, bookkeeping.To, taxableSales,
            bookkeeping.GstOnSales, taxableFees, bookkeeping.GstOnFees,
            bookkeeping.GstOnSales - bookkeeping.GstOnFees - operatingExpenseGst, quality)
        {
            OperatingExpenseGst = operatingExpenseGst
        };
    }

    public async Task<DashboardReportDto> GetDashboardAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default)
    {
        var range = ResolveRange(filter);
        var machineId = MachineId(filter);
        var isMachineFiltered = machineId.HasValue;
        var summary = await CostQuery(range, machineId).GroupBy(_ => 1)
            .Select(g => new
            {
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Count(),
                PartialCost = g.Sum(x => x.CostOfGoodsSold ?? 0m),
                IsCogsComplete = g.All(x => x.HasCost),
                UncostedTransactionCount = g.Count(x => !x.HasCost),
                UncostedSalesAmount = g.Where(x => !x.HasCost).Sum(x => x.SettlementValue),
                Transactions = g.Count(),
                Machines = g.Select(x => x.MachineID).Distinct().Count(),
                Products = g.Select(x => x.NayaxProductId).Where(x => x.HasValue).Distinct().Count()
            }).SingleOrDefaultAsync(cancellationToken);
        var productReport = await GetProductProfitabilityAsync(filter, cancellationToken);
        var paymentSummary = await GetPaymentSummaryAsync(range, machineId, cancellationToken);
        var imported = await ImportedSummaryAsync(range, machineId, cancellationToken);
        var processingFees = await _nayaxProcessingFees.GetProcessingFeesAsync(range.From, range.ToDate, machineId, cancellationToken);
        var commissions = await GetMachineCommissionsAsync(range, machineId, cancellationToken);
        var siteCommission = await GetSiteCommissionAsync(range, machineId, commissions, cancellationToken);
        var receiptCosts = isMachineFiltered ? new ReceiptCostSummary(0m, 0m) : await ReceiptCostsAsync(range, cancellationToken);
        var operatingExpenses = await OperatingExpenseSummaryAsync(range, machineId, cancellationToken);
        var fees = processingFees.TotalFeeIncGst;
        var totalSales = paymentSummary.GrossSales;
        var partialCostOfGoods = summary?.PartialCost ?? 0m;
        var isCogsComplete = summary?.IsCogsComplete ?? true;
        decimal? costOfGoods = isCogsComplete ? partialCostOfGoods : null;
        decimal? grossProfit = isCogsComplete ? ReportingCalculations.GrossProfit(totalSales, partialCostOfGoods) : null;
        var otherOperatingExpenses = operatingExpenses.Total;
        var commissionCompleteForScope = isMachineFiltered
            ? commissions.Machines.GetValueOrDefault(machineId!.Value).IsComplete
            : commissions.IsComplete;
        var isProfitComplete = grossProfit.HasValue && !processingFees.HasMissingRates && commissionCompleteForScope;
        decimal? directProfit = isMachineFiltered && isProfitComplete
            ? grossProfit!.Value - fees - siteCommission - otherOperatingExpenses
            : null;
        decimal? netProfit = !isMachineFiltered && isProfitComplete
            ? grossProfit.Value - fees - siteCommission - receiptCosts.Total - otherOperatingExpenses
            : null;
        var expectedReimbursement = paymentSummary.CardSales - fees;
        var actualReimbursement = imported.NetSettlement;
        var reimbursementDifference = actualReimbursement - expectedReimbursement;
        var reconciliationStatus = !imported.ContainsRows
            ? "Pending"
            : Math.Abs(reimbursementDifference) <= 0.01m ? "Reconciled" : "Needs Review";
        var dashboardQuality = productReport.DataQuality;
        if (!isCogsComplete)
        {
            var notes = dashboardQuality.Notes?.ToList() ?? [];
            notes.Add("One or more completed sales have no persisted COGS; profitability is unavailable.");
            dashboardQuality = dashboardQuality with { Notes = notes };
        }
        if (processingFees.HasMissingRates)
        {
            var notes = dashboardQuality.Notes?.ToList() ?? [];
            notes.Add($"{processingFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; profit is unavailable.");
            dashboardQuality = dashboardQuality with { Notes = notes };
        }
        var commissionNotes = dashboardQuality.Notes?.ToList() ?? [];
        if (isMachineFiltered)
            commissionNotes.Add("Net profit is unavailable for a machine-filtered report because shared business overhead is not allocated to individual machines.");
        AddCommissionQualityNotes(commissionNotes, commissions, isMachineFiltered ? "direct profit" : "net profit");
        dashboardQuality = dashboardQuality with { Notes = commissionNotes };
        return new DashboardReportDto(range.From, range.ToDate, summary?.Sales ?? 0m,
            grossProfit, summary?.Transactions ?? 0,
            summary?.Quantity ?? 0m, summary?.Machines ?? 0, summary?.Products ?? 0,
            productReport.Rows.Count(x => x.IsUnmapped), dashboardQuality, fees,
            actualReimbursement,
            siteCommission, netProfit,
            netProfit.HasValue ? ReportingCalculations.MarginPercent(totalSales, partialCostOfGoods + fees + siteCommission + receiptCosts.Total + otherOperatingExpenses) : null,
            processingFees.TotalFeeExGst, receiptCosts.Delivery, receiptCosts.Package, otherOperatingExpenses,
            paymentSummary.CardSales, paymentSummary.CashSales, paymentSummary.CardTransactions, paymentSummary.CashTransactions)
        {
            TotalSales = totalSales,
            CostOfGoodsSold = costOfGoods,
            PartialCostOfGoods = partialCostOfGoods,
            IsCogsComplete = isCogsComplete,
            UncostedTransactionCount = summary?.UncostedTransactionCount ?? 0,
            UncostedSalesAmount = summary?.UncostedSalesAmount ?? 0m,
            AverageSale = ReportingCalculations.Average(totalSales, summary?.Transactions ?? 0),
            GrossMarginPercent = grossProfit.HasValue ? ReportingCalculations.MarginPercent(totalSales, partialCostOfGoods) : null,
            NayaxFeesIncludingGst = fees,
            OtherOperatingExpenses = otherOperatingExpenses,
            ExpectedReimbursement = expectedReimbursement,
            ActualReimbursement = actualReimbursement,
            ReimbursementDifference = reimbursementDifference,
            IsReconciled = imported.ContainsRows && Math.Abs(reimbursementDifference) <= 0.01m,
            ReconciliationStatus = reconciliationStatus,
            ReconciliationTolerance = 0.01m,
            AdjustmentsSupported = false,
            DirectProfit = directProfit,
            DirectMarginPercent = directProfit.HasValue
                ? ReportingCalculations.MarginPercent(totalSales, partialCostOfGoods + fees + siteCommission + otherOperatingExpenses)
                : null,
            StructuredOperatingExpenses = operatingExpenses.Total,
            OperatingExpenseGst = operatingExpenses.Gst,
            NayaxProcessingFees = processingFees
        };
    }

    public Task<TransactionSalesReportDto> GetTransactionsAsync(
        TransactionSalesFilterDto filter, CancellationToken cancellationToken = default) =>
        GetTransactionsInternalAsync(filter, paginate: true, cancellationToken);

    private async Task<TransactionSalesReportDto> GetTransactionsInternalAsync(
        TransactionSalesFilterDto filter, bool paginate, CancellationToken cancellationToken)
    {
        var range = new DateRange((filter.From ?? new DateTime(1900, 1, 1)).Date,
            (filter.To ?? new DateTime(9999, 12, 30)).Date);
        if (range.ToDate < range.From) range = new DateRange(range.ToDate, range.From);

        var sales = await AllSalesQuery(range, filter.MachineId).ToListAsync(cancellationToken);
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        var rates = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .Where(x => x.EffectiveFrom <= range.ToDate)
            .OrderBy(x => x.EffectiveFrom)
            .ToListAsync(cancellationToken);
        var agreements = await _db.SiteCommissionAgreements.AsNoTracking()
            .OrderBy(x => x.EffectiveFrom)
            .ToListAsync(cancellationToken);

        var siteMappingUnavailable = _nayaxLynxClient is null;
        var liveMachines = new List<NayaxMachine>();
        if (_nayaxLynxClient is not null)
        {
            try
            {
                liveMachines = await _nayaxLynxClient.GetMachinesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                siteMappingUnavailable = true;
            }
        }

        var machineById = liveMachines.GroupBy(x => x.MachineID).ToDictionary(x => x.Key, x => x.First());
        var machinesBySite = liveMachines.Where(x => x.CustomerID.HasValue)
            .GroupBy(x => x.CustomerID!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());
        var details = sales.Select(sale =>
        {
            machineById.TryGetValue(sale.MachineID, out var machine);
            var product = NayaxProductMatcher.Match(products, sale.NayaxProductId, sale.ProductName);
            var siteId = machine?.CustomerID;
            return new TransactionSaleDetail(
                sale,
                product?.Id,
                product?.Name ?? (string.IsNullOrWhiteSpace(sale.ProductName) ? "Unmapped product" : NayaxProductMatcher.NormalizeName(sale.ProductName)),
                siteId,
                siteId.HasValue && machinesBySite.TryGetValue(siteId.Value, out var siteMachines)
                    ? SiteNameResolver.FromMachines(siteMachines, siteId.Value)
                    : null,
                PaymentMethodClassifier.Classify(sale.PaymentMethod),
                NayaxTransactionStatusClassifier.Classify(sale.TransactionStatusId),
                sale.CostingStatus == SaleCostingStatus.Costed && sale.CostOfGoodsSold.HasValue);
        }).ToList();

        var options = new TransactionSalesFilterOptionsDto(
            details.Where(x => x.SiteId.HasValue).GroupBy(x => x.SiteId!.Value)
                .Select(x => new TransactionSalesFilterOptionDto(x.Key, x.First().SiteName ?? $"Site {x.Key}"))
                .OrderBy(x => x.Name).ToList(),
            details.Where(x => x.ProductId.HasValue).GroupBy(x => x.ProductId!.Value)
                .Select(x => new TransactionSalesFilterOptionDto(x.Key, x.First().ProductName))
                .OrderBy(x => x.Name).ToList());

        var status = FilterValue(filter.Status);
        if (string.IsNullOrWhiteSpace(status)) status = "completed";
        var payment = FilterValue(filter.PaymentType);
        var cogs = FilterValue(filter.CogsStatus);
        var search = filter.Search?.Trim();
        var filtered = details.Where(x =>
            (!filter.SiteId.HasValue || x.SiteId == filter.SiteId) &&
            (!filter.ProductId.HasValue || x.ProductId == filter.ProductId) &&
            MatchesPayment(payment, x.PaymentType) &&
            MatchesStatus(status, x.Status) &&
            MatchesCogs(cogs, x.IsCosted) &&
            MatchesSearch(search, x)).ToList();

        var builds = filtered.Select(x => CreateTransactionRow(x, rates, agreements)).ToList();
        var rows = SortTransactionRows(builds.Select(x => x.Row), filter.SortBy, filter.SortDescending).ToList();
        var completedRows = rows.Where(x => x.IsCompleted).ToList();
        var costedRows = completedRows.Where(x => x.CostingStatus == SaleCostingStatus.Costed.ToString() && x.CostOfGoods.HasValue).ToList();
        var isCogsComplete = completedRows.All(x => x.CostingStatus == SaleCostingStatus.Costed.ToString() && x.CostOfGoods.HasValue);
        var partialCost = costedRows.Sum(x => x.CostOfGoods!.Value);
        decimal? cost = isCogsComplete ? partialCost : null;
        decimal? grossProfit = isCogsComplete ? completedRows.Sum(x => x.GrossProfit ?? 0m) : null;
        var directComplete = isCogsComplete && completedRows.All(x => x.DirectProfit.HasValue);
        decimal? directProfit = directComplete ? completedRows.Sum(x => x.DirectProfit!.Value) : null;
        var completedSales = completedRows.Sum(x => x.Sale);
        decimal? partialGross = costedRows.Count == 0 ? null : costedRows.Sum(x => x.GrossProfit!.Value);
        var directRows = completedRows.Where(x => x.DirectProfit.HasValue).ToList();
        decimal? partialDirect = directRows.Count == 0 ? null : directRows.Sum(x => x.DirectProfit!.Value);
        var totals = new TransactionSalesTotalsDto(
            rows.Count, completedRows.Count, rows.Sum(x => x.Sale),
            rows.Where(x => x.PaymentType == NayaxPaymentType.Card.ToString()).Sum(x => x.Sale),
            rows.Where(x => x.PaymentType == NayaxPaymentType.Cash.ToString()).Sum(x => x.Sale),
            costedRows.Count, completedRows.Count - costedRows.Count, isCogsComplete,
            cost, partialCost, grossProfit,
            grossProfit.HasValue ? ReportingCalculations.PercentageOf(grossProfit.Value, completedSales) : null,
            directProfit,
            directProfit.HasValue ? ReportingCalculations.PercentageOf(directProfit.Value, completedSales) : null,
            partialGross, partialDirect,
            rows.Where(x => x.FeeSource == "Estimated").Sum(x => x.FeeExGst ?? 0m),
            rows.Where(x => x.FeeSource == "Estimated").Sum(x => x.FeeGst ?? 0m),
            rows.Where(x => x.FeeSource == "Estimated").Sum(x => x.FeeIncGst ?? 0m),
            completedRows.Sum(x => x.CommissionAmount ?? 0m));

        var notes = new List<string>();
        var missingStatus = rows.Count(x => x.TransactionStatusId is null);
        if (missingStatus > 0) notes.Add($"{missingStatus} transaction(s) have no Nayax status ID.");
        if (totals.UncostedCompletedTransactionCount > 0)
            notes.Add($"{totals.UncostedCompletedTransactionCount} completed transaction(s) have incomplete persisted COGS; full profit totals are unavailable.");
        if (rows.Any(x => x.ProductId is null)) notes.Add("One or more transactions could not be mapped to a catalogue product.");
        if (siteMappingUnavailable && rows.Count > 0)
            notes.Add("Current site mapping is unavailable because it comes only from the live Nayax machine CustomerID.");
        if (builds.Any(x => x.FeeUnavailable))
            notes.Add("An effective-dated estimated card fee is unavailable for one or more completed card transactions.");
        if (builds.Any(x => x.HasOverlappingCommission))
            notes.Add("Overlapping site commission agreements cover one or more transactions; their direct profit is unavailable.");
        if (builds.Any(x => x.CommissionUnavailable))
            notes.Add("Site mapping or effective commission agreement coverage is unavailable for one or more completed transactions.");
        if (rows.Any(x => x.FeeSource == "Estimated"))
            notes.Add("Transaction fees are configured estimates. Imported Nayax fees are period/device-level and are not allocated to transactions.");
        var nayaxCosted = rows.Count(x => x.CostSource == "Nayax Historical Export");
        if (nayaxCosted > 0)
            notes.Add($"Historical COGS includes {nayaxCosted} transaction(s) costed from the Nayax transaction export.");
        var quality = new ReportingDataQualityDto(
            MissingStatus: missingStatus > 0,
            HistoricalCostUnavailable: totals.UncostedCompletedTransactionCount > 0,
            GstClassificationMissing: false,
            CommissionNotPersisted: false,
            ContainsUnmappedProducts: rows.Any(x => x.ProductId is null),
            Notes: notes);

        var pageSize = filter.PageSize is 50 or 100 or 250 ? filter.PageSize : 50;
        var page = Math.Max(1, filter.Page);
        var resultRows = paginate ? rows.Skip((page - 1) * pageSize).Take(pageSize).ToList() : rows;
        return new TransactionSalesReportDto(range.From, range.ToDate, resultRows, totals, quality,
            page, pageSize, rows.Count, options);
    }

    private static TransactionRowBuild CreateTransactionRow(
        TransactionSaleDetail detail, IReadOnlyList<NayaxProcessingFeeRate> rates,
        IReadOnlyList<SiteCommissionAgreement> agreements)
    {
        var sale = detail.Sale;
        decimal? feeExGst = 0m, feeGst = 0m, feeIncGst = 0m;
        var feeSource = "Not applicable";
        var feeUnavailable = false;
        if (detail.Status == NayaxTransactionStatus.Completed && detail.PaymentType == NayaxPaymentType.Card)
        {
            var rate = rates.LastOrDefault(x => x.EffectiveFrom.Date <= detail.Sale.MachineAuthorizationTime.Date);
            if (rate is null)
            {
                feeExGst = feeGst = feeIncGst = null;
                feeSource = "Unavailable";
                feeUnavailable = true;
            }
            else
            {
                feeExGst = rate.FeeExGst;
                feeGst = ReportingCalculations.GstFromExcluding(rate.FeeExGst);
                feeIncGst = feeExGst + feeGst;
                feeSource = "Estimated";
            }
        }
        else if (detail.Status == NayaxTransactionStatus.Completed && detail.PaymentType == NayaxPaymentType.Unknown)
        {
            feeExGst = feeGst = feeIncGst = null;
            feeSource = "Unavailable";
            feeUnavailable = true;
        }

        SiteCommissionAgreement? agreement = null;
        var hasOverlappingCommission = false;
        var commissionUnavailable = false;
        if (detail.Status == NayaxTransactionStatus.Completed && detail.SiteId.HasValue)
        {
            var siteAgreements = agreements.Where(x => x.SiteId == detail.SiteId.Value).ToList();
            try
            {
                agreement = EffectiveFinancialConfiguration.ResolveAgreement(
                    siteAgreements, detail.SiteId.Value, detail.Sale.MachineAuthorizationTime);
                commissionUnavailable = agreement is null && siteAgreements.Count > 0;
            }
            catch (InvalidOperationException)
            {
                hasOverlappingCommission = true;
                commissionUnavailable = true;
            }
        }
        else if (detail.Status == NayaxTransactionStatus.Completed)
        {
            commissionUnavailable = true;
        }
        decimal? commissionAmount = agreement is null ? (commissionUnavailable ? null : 0m) :
            SiteCommissionCalculator.CommissionAmount(agreement, sale.SettlementValue, detail.PaymentType);
        var isCosted = detail.IsCosted && detail.Status == NayaxTransactionStatus.Completed;
        decimal? grossProfit = isCosted ? sale.SettlementValue - detail.Sale.CostOfGoodsSold!.Value : null;
        decimal? directProfit = grossProfit.HasValue && feeIncGst.HasValue && commissionAmount.HasValue
            ? grossProfit.Value - feeIncGst.Value - commissionAmount.Value
            : null;
        return new TransactionRowBuild(new TransactionSalesRowDto(
            detail.Sale.MachineAuthorizationTime, detail.Sale.TransactionID, detail.Sale.MachineID,
            detail.Sale.MachineName ?? $"Machine {detail.Sale.MachineID}", detail.SiteId, detail.SiteName,
            detail.ProductId, detail.ProductName, detail.PaymentType.ToString(), detail.Sale.PaymentMethod,
            sale.SettlementValue, detail.Sale.NayaxProductCostPrice, detail.Sale.UnitCostAtSale,
            detail.Sale.CostOfGoodsSold, detail.Sale.CostingStatus.ToString(),
            CostSourceLabel(detail.Sale), grossProfit,
            grossProfit.HasValue ? ReportingCalculations.PercentageOf(grossProfit.Value, sale.SettlementValue) : null,
            directProfit,
            directProfit.HasValue ? ReportingCalculations.PercentageOf(directProfit.Value, sale.SettlementValue) : null,
            feeExGst, feeGst, feeIncGst, feeSource,
            agreement?.CommissionRate, agreement?.Basis.ToString(), commissionAmount,
            detail.Sale.TransactionStatusId, NayaxTransactionStatusClassifier.Describe(detail.Sale.TransactionStatusId),
            detail.Status == NayaxTransactionStatus.Completed), feeUnavailable, hasOverlappingCommission,
            commissionUnavailable);
    }

    private static string CostSourceLabel(NayaxSales sale)
    {
        if (sale.CostingStatus == SaleCostingStatus.Pending)
            return "Pending";
        if (sale.CostingStatus == SaleCostingStatus.LegacyEstimated)
            return "Estimated";

        return sale.CostSource switch
        {
            SaleCostSource.InventoryLedger => "Inventory Ledger",
            SaleCostSource.NayaxTransactionExport => "Nayax Historical Export",
            SaleCostSource.Estimated => "Estimated",
            _ => "Unknown"
        };
    }

    private static IEnumerable<TransactionSalesRowDto> SortTransactionRows(
        IEnumerable<TransactionSalesRowDto> rows, string? sortBy, bool descending)
    {
        var key = FilterValue(sortBy);
        return (key, descending) switch
        {
            ("machine", true) => rows.OrderByDescending(x => x.MachineName).ThenByDescending(x => x.TransactionId),
            ("machine", false) => rows.OrderBy(x => x.MachineName).ThenBy(x => x.TransactionId),
            ("product", true) => rows.OrderByDescending(x => x.ProductName).ThenByDescending(x => x.TransactionId),
            ("product", false) => rows.OrderBy(x => x.ProductName).ThenBy(x => x.TransactionId),
            ("sale", true) => rows.OrderByDescending(x => x.Sale).ThenByDescending(x => x.TransactionId),
            ("sale", false) => rows.OrderBy(x => x.Sale).ThenBy(x => x.TransactionId),
            ("cogs", true) => rows.OrderByDescending(x => x.CostOfGoods).ThenByDescending(x => x.TransactionId),
            ("cogs", false) => rows.OrderBy(x => x.CostOfGoods).ThenBy(x => x.TransactionId),
            ("gross" or "grossprofit", true) => rows.OrderByDescending(x => x.GrossProfit).ThenByDescending(x => x.TransactionId),
            ("gross" or "grossprofit", false) => rows.OrderBy(x => x.GrossProfit).ThenBy(x => x.TransactionId),
            ("direct" or "directprofit", true) => rows.OrderByDescending(x => x.DirectProfit).ThenByDescending(x => x.TransactionId),
            ("direct" or "directprofit", false) => rows.OrderBy(x => x.DirectProfit).ThenBy(x => x.TransactionId),
            ("status", true) => rows.OrderByDescending(x => x.TransactionStatus).ThenByDescending(x => x.TransactionId),
            ("status", false) => rows.OrderBy(x => x.TransactionStatus).ThenBy(x => x.TransactionId),
            (_, false) => rows.OrderBy(x => x.TransactionDate).ThenBy(x => x.TransactionId),
            _ => rows.OrderByDescending(x => x.TransactionDate).ThenByDescending(x => x.TransactionId)
        };
    }

    private static bool MatchesPayment(string value, NayaxPaymentType paymentType) =>
        value is "" or "all" || value == FilterValue(paymentType.ToString());

    private static bool MatchesStatus(string value, NayaxTransactionStatus status) =>
        value is "" or "all" || value == FilterValue(status.ToString()) ||
        (value is "cancelled" or "declined" && status == NayaxTransactionStatus.CancelledOrDeclined);

    private static bool MatchesCogs(string value, bool isCosted) =>
        value is "" or "all" || (value == "costed" && isCosted) ||
        (value is "uncosted" or "incomplete" && !isCosted);

    private static bool MatchesSearch(string? search, TransactionSaleDetail detail) =>
        string.IsNullOrWhiteSpace(search) ||
        detail.Sale.TransactionID.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase) ||
        detail.Sale.MachineID.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (detail.Sale.MachineName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        detail.ProductName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (detail.SiteName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (detail.Sale.PaymentMethod?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string FilterValue(string? value) =>
        value?.Trim().Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() ?? string.Empty;

    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

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

    public async Task<byte[]> ExportCsvAsync(string report, TransactionSalesFilterDto filter, CancellationToken cancellationToken = default)
    {
        var rows = await ExportTransactionRowsAsync(report, filter, cancellationToken);
        var builder = new StringBuilder();
        foreach (var row in rows)
            builder.AppendLine(string.Join(",", row.Select(Csv)));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public async Task<byte[]> ExportXlsxAsync(string report, TransactionSalesFilterDto filter, CancellationToken cancellationToken = default)
    {
        var rows = await ExportTransactionRowsAsync(report, filter, cancellationToken);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Transactions");
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                sheet.Cell(r + 1, c + 1).Value = rows[r][c];
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<List<List<string>>> ExportTransactionRowsAsync(
        string report, TransactionSalesFilterDto filter, CancellationToken cancellationToken)
    {
        if (report.Trim().ToLowerInvariant() is not ("transactions" or "transaction-sales"))
            throw new ArgumentException("Unsupported transaction report.", nameof(report));

        var value = await GetTransactionsInternalAsync(filter, paginate: false, cancellationToken);
        var rows = new List<List<string>>
        {
            new()
            {
                "TransactionDate", "TransactionId", "MachineId", "Machine", "SiteId", "Site",
                "ProductId", "Product", "PaymentType", "RawPaymentMethod", "Sale", "UnitCostAtSale",
                "NayaxProductCostPrice", "CostOfGoods", "CostingStatus", "CostSource",
                "GrossProfit", "GrossMarginPercent", "DirectProfit",
                "DirectMarginPercent", "FeeExGst", "FeeGST", "FeeIncGST", "FeeSource",
                "CommissionRate", "CommissionBasis", "CommissionAmount", "TransactionStatusId",
                "TransactionStatus", "IsCompleted"
            }
        };
        rows.AddRange(value.Rows.Select(x => new List<string>
        {
            x.TransactionDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            x.TransactionId.ToString(CultureInfo.InvariantCulture), x.MachineId.ToString(CultureInfo.InvariantCulture),
            x.MachineName, Number(x.SiteId), x.SiteName ?? string.Empty, Number(x.ProductId), x.ProductName,
            x.PaymentType, x.RawPaymentMethod ?? string.Empty, Number(x.Sale), Number(x.UnitCostAtSale),
            Number(x.NayaxProductCostPrice), Number(x.CostOfGoods), x.CostingStatus, x.CostSource,
            Number(x.GrossProfit), Number(x.GrossMarginPercent),
            Number(x.DirectProfit), Number(x.DirectMarginPercent), Number(x.FeeExGst), Number(x.FeeGst),
            Number(x.FeeIncGst), x.FeeSource, Number(x.CommissionRate), x.CommissionBasis ?? string.Empty,
            Number(x.CommissionAmount), Number(x.TransactionStatusId), x.TransactionStatus, x.IsCompleted.ToString()
        }));
        return rows;
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
                    "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "FeeSource", "ImportedReimbursement",
                    "NetReimbursement", "ReconciliationStatus"
                } }
                .Concat(value.Rows.Select(x => new List<string>
                {
                    x.Date.ToString("yyyy-MM-dd"), x.GrossSales.ToString(CultureInfo.InvariantCulture),
                    x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture),
                    x.AverageSale.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture),
                    Number(x.CostOfGoods), Number(x.GrossProfit),
                    Number(x.GrossMarginPercent), x.TransactionCount.ToString(),
                    x.IsCogsComplete.ToString(), x.UncostedTransactionCount.ToString(),
                    x.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    x.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), (x.NayaxFeesIncludingGst - x.NayaxFeesExGst).ToString(CultureInfo.InvariantCulture), x.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture), x.NayaxFeeSource,
                    x.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), x.NetReimbursement.ToString(CultureInfo.InvariantCulture),
                    x.ReconciliationStatus
                })).ToList();
            if (value.Totals is not null)
                rows.Add(new List<string>
                {
                    "TOTAL", value.Totals.GrossSales.ToString(CultureInfo.InvariantCulture),
                    value.Totals.CardSales.ToString(CultureInfo.InvariantCulture), value.Totals.CashSales.ToString(CultureInfo.InvariantCulture),
                    value.Totals.AverageSale.ToString(CultureInfo.InvariantCulture), value.Totals.Quantity.ToString(CultureInfo.InvariantCulture),
                    Number(value.Totals.CostOfGoods), Number(value.Totals.GrossProfit),
                    Number(value.Totals.GrossMarginPercent), value.Totals.TransactionCount.ToString(),
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
            var isMachineFiltered = MachineId(filter).HasValue;
            return new List<List<string>>
            {
                new() { "From", "To", "FinancialYear", "GrossSales", "CardSales", "CashSales", "CardTransactions", "CashTransactions", "COGS", "GrossProfit",                 "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "DeliveryCosts", "PackageCosts", "OtherOperatingExpenses", "NetSettlement", "SiteCommission", isMachineFiltered ? "DirectProfit" : "NetProfit", isMachineFiltered ? "DirectMarginPercent" : "NetMargin", "GstOnSales", "GstOnFees" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.FinancialYear, value.Sales.ToString(CultureInfo.InvariantCulture), value.CardSales.ToString(CultureInfo.InvariantCulture), value.CashSales.ToString(CultureInfo.InvariantCulture), value.CardTransactionCount.ToString(), value.CashTransactionCount.ToString(), Number(value.CostOfGoods), Number(value.GrossProfit), value.NayaxProcessingFees.TotalFeeExGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.TotalFeeGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.TotalFeeIncGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.ActualFeeIncGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.EstimatedFeeIncGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.EstimatedCardTransactionCount.ToString(), value.NayaxProcessingFees.HasEstimatedFees.ToString(), value.DeliveryCosts.ToString(CultureInfo.InvariantCulture), value.PackageCosts.ToString(CultureInfo.InvariantCulture), value.OtherOperatingExpenses.ToString(CultureInfo.InvariantCulture), value.NetSettlement.ToString(CultureInfo.InvariantCulture), value.SiteCommission.ToString(CultureInfo.InvariantCulture), Number(isMachineFiltered ? value.DirectProfit : value.NetProfit), Number(isMachineFiltered ? value.DirectMarginPercent : value.NetMarginPercent), value.GstOnSales.ToString(CultureInfo.InvariantCulture), value.GstOnFees.ToString(CultureInfo.InvariantCulture) }
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
            return new[] { new List<string> { "MachineId", "MachineName", "Sales", "CardSales", "CashSales", "Quantity", "CostOfGoods", "GrossProfit", "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "CommissionPercent", "SiteCommission", "DirectProfit", "DirectMarginPercent", "Transactions" } }
                .Concat(value.Rows.Select(x => new List<string> { x.MachineId.ToString(), x.MachineName, x.Sales.ToString(CultureInfo.InvariantCulture), x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), Number(x.CostOfGoods), Number(x.GrossProfit), x.NayaxProcessingFees.TotalFeeExGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.TotalFeeGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.TotalFeeIncGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.ActualFeeIncGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.EstimatedFeeIncGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.EstimatedCardTransactionCount.ToString(), x.NayaxProcessingFees.HasEstimatedFees.ToString(), x.CommissionPercent.ToString(CultureInfo.InvariantCulture), x.SiteCommission.ToString(CultureInfo.InvariantCulture), Number(x.DirectProfit), Number(x.DirectMarginPercent), x.TransactionCount.ToString(CultureInfo.InvariantCulture) })).ToList();
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
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.Sales.ToString(CultureInfo.InvariantCulture), Number(value.GrossProfit), value.Transactions.ToString(CultureInfo.InvariantCulture), value.Quantity.ToString(CultureInfo.InvariantCulture), value.MachineCount.ToString(), value.ProductCount.ToString(), value.UnmappedProductCount.ToString() }
            };
        }
        var products = await GetProductProfitabilityAsync(filter, cancellationToken);
        return new[] { new List<string> { "Product", "Sales", "Quantity", "CostOfGoods", "GrossProfit", "MarginPercent", "Transactions", "Unmapped" } }
            .Concat(products.Rows.Select(x => new List<string> { x.ProductName, x.Sales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), Number(x.CostOfGoods), Number(x.GrossProfit), Number(x.MarginPercent), x.TransactionCount.ToString(CultureInfo.InvariantCulture), x.IsUnmapped.ToString() })).ToList();
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
            ProductName = sale.ProductName, PaymentMethod = sale.PaymentMethod, SettlementValue = sale.SettlementValue,
            MachineAuthorizationTime = sale.MachineAuthorizationTime,
            Cost = sale.CostOfGoodsSold ?? 0m,
            CostOfGoodsSold = sale.CostOfGoodsSold,
            HasCost = sale.CostOfGoodsSold.HasValue,
            HasCompleteCost = sale.CostingStatus == SaleCostingStatus.Costed
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

    private async Task<OperatingExpenseSummary> OperatingExpenseSummaryAsync(DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        var query = _db.OperatingExpenses.AsNoTracking()
            .Where(x => x.ExpenseDate >= range.From && x.ExpenseDate < range.EndExclusive &&
                (!machineId.HasValue || x.MachineId == machineId.Value));
        var rows = await query.Select(x => new { x.Category, x.TotalAmount, x.GstAmount }).ToListAsync(cancellationToken);
        return new OperatingExpenseSummary(rows.Sum(x => x.TotalAmount), rows.Sum(x => x.GstAmount),
            rows.GroupBy(x => x.Category.ToString()).ToDictionary(g => g.Key, g => g.Sum(x => x.TotalAmount)));
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

    private async Task<CommissionResolutionResult> GetMachineCommissionsAsync(DateRange range, long? machineId, CancellationToken cancellationToken)
    {
        var report = await _siteCommissions.GetReportAsync(range.From, range.ToDate, null, cancellationToken);

        var relevantRows = report.Rows.Where(site => !machineId.HasValue || site.Machines.Any(machine => machine.MachineId == machineId.Value)).ToList();
        var machines = relevantRows.SelectMany(site => site.Machines.Select(machine =>
            (machine.MachineId, new MachineCommission(site.CommissionRate, machine.CommissionDue, machine.IsComplete))))
            .ToDictionary(x => x.MachineId, x => x.Item2);
        var selectedMachine = machineId.HasValue
            ? relevantRows.SelectMany(x => x.Machines).SingleOrDefault(x => x.MachineId == machineId.Value)
            : null;
        var warnings = machineId.HasValue
            ? SelectedMachineCommissionWarnings(selectedMachine)
            : relevantRows.Where(x => !string.IsNullOrWhiteSpace(x.DataQuality))
                .Select(x => x.DataQuality!).Distinct().ToList();
        var hasConfigurationGap = machineId.HasValue
            ? selectedMachine?.HasConfigurationGap ?? false
            : relevantRows.Any(x => x.HasConfigurationGap);
        var hasOverlap = machineId.HasValue
            ? selectedMachine?.HasOverlap ?? false
            : relevantRows.Any(x => x.HasOverlap);
        var usesMultipleRates = !machineId.HasValue && relevantRows.Any(x => x.UsesMultipleRates);
        var hasMissingSiteMapping = false;
        var saleMachineIds = await SalesQuery(range, machineId).Select(x => x.MachineID).Distinct().ToListAsync(cancellationToken);
        if (saleMachineIds.Any(id => !machines.ContainsKey(id)))
        {
            warnings.Add("Current site mapping is unavailable for one or more completed sales; commission and profitability are incomplete.");
            hasMissingSiteMapping = true;
        }
        return new(machines, !hasConfigurationGap && !hasOverlap && !hasMissingSiteMapping,
            hasConfigurationGap, hasOverlap, hasMissingSiteMapping, usesMultipleRates, warnings.Distinct().ToList());
    }

    private static List<string> SelectedMachineCommissionWarnings(SiteCommissionMachineDto? machine)
    {
        var warnings = new List<string>();
        if (machine?.HasConfigurationGap == true)
            warnings.Add("Commission agreements exist but do not cover one or more sales for the selected machine.");
        if (machine?.HasOverlap == true)
            warnings.Add("Overlapping commission agreements cover one or more sales for the selected machine.");
        return warnings;
    }

    private async Task<decimal> GetSiteCommissionAsync(DateRange range, long? machineId, CommissionResolutionResult commissions, CancellationToken cancellationToken)
    {
        var salesByMachine = await SalesQuery(range, machineId)
            .GroupBy(x => x.MachineID)
            .Select(g => new { MachineId = g.Key, Sales = g.Sum(x => x.SettlementValue) })
            .ToListAsync(cancellationToken);
        return salesByMachine.Sum(x => commissions.Machines.GetValueOrDefault(x.MachineId).Due);
    }

    private static void AddCommissionQualityNotes(List<string> notes, CommissionResolutionResult commissions, string profitLabel = "profit")
    {
        if (!commissions.IsComplete)
            notes.Add($"Commission configuration is incomplete; {profitLabel} is unavailable.");
        notes.AddRange(commissions.Warnings);
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
            new[] { "Nayax status IDs are stored raw; existing historical rows were backfilled to status 12 by migration; rows still missing a status are excluded.", "Historical COGS uses the persisted sale cost; unresolved completed sales are reported as incomplete.", "Commission is calculated from effective-dated site commission agreements.", "GST classification is not persisted on sales; GST amounts are an indicative 10% inclusive calculation." }
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

    private sealed record OperatingExpenseSummary(decimal Total, decimal Gst, IReadOnlyDictionary<string, decimal> ByCategory);

    private sealed class SaleCost
    {
        public long MachineID { get; set; }
        public string? MachineName { get; set; }
        public long? NayaxProductId { get; set; }
        public string? ProductName { get; set; }
        public string? PaymentMethod { get; set; }
        public decimal SettlementValue { get; set; }
        public DateTime MachineAuthorizationTime { get; set; }
        public decimal Cost { get; set; }
        public bool HasCost { get; set; }
        public decimal? CostOfGoodsSold { get; set; }
        public bool HasCompleteCost { get; set; }
    }

    private sealed record TransactionSaleDetail(
        NayaxSales Sale,
        long? ProductId,
        string ProductName,
        long? SiteId,
        string? SiteName,
        NayaxPaymentType PaymentType,
        NayaxTransactionStatus Status,
        bool IsCosted);

    private sealed record TransactionRowBuild(
        TransactionSalesRowDto Row,
        bool FeeUnavailable,
        bool HasOverlappingCommission,
        bool CommissionUnavailable);

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

    private readonly record struct MachineCommission(decimal Percent, decimal Due, bool IsComplete = false);
    private sealed record CommissionResolutionResult(
        IReadOnlyDictionary<long, MachineCommission> Machines,
        bool IsComplete,
        bool HasConfigurationGap,
        bool HasOverlap,
        bool HasMissingSiteMapping,
        bool UsesMultipleRates,
        IReadOnlyList<string> Warnings);
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
    public static decimal GstFromExcluding(decimal amount) => amount * 10m / 100m;
    public static decimal Average(decimal amount, int count) => count == 0 ? 0m : amount / count;
}
