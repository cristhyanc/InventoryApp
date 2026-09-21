using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Reporting;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using static Inventory.Application.Reporting.Shared.ReportingQuality;

namespace InventoryApi.Services;

public sealed class ReportingService : IReportingService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient? _nayaxLynxClient;
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;
    private readonly ISiteCommissionService _siteCommissions;
    private readonly GetBookkeepingReport _getBookkeepingReport;
    private readonly GetDailyReport _getDailyReport;
    private readonly GetReconciliationReport _getReconciliationReport;

    public ReportingService(AppDbContext db, INayaxProcessingFeeService nayaxProcessingFees,
        ISiteCommissionService siteCommissions, GetBookkeepingReport getBookkeepingReport,
        GetDailyReport getDailyReport, GetReconciliationReport getReconciliationReport,
        INayaxLynxClient? nayaxLynxClient = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _nayaxProcessingFees = nayaxProcessingFees;
        _siteCommissions = siteCommissions;
        _getBookkeepingReport = getBookkeepingReport;
        _getDailyReport = getDailyReport;
        _getReconciliationReport = getReconciliationReport;
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

    public Task<BookkeepingReportDto> GetBookkeepingAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        _getBookkeepingReport.Handle(filter, cancellationToken);

    public Task<DailyReportDto> GetDailyAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default) =>
        _getDailyReport.Handle(filter, cancellationToken);

    public Task<ReconciliationReportDto> GetReconciliationAsync(ReportingFilterDto filter, decimal tolerance = 0.01m, CancellationToken cancellationToken = default) =>
        _getReconciliationReport.Handle(filter, tolerance, cancellationToken);

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
            CostOfGoodsSold = sale.CostOfGoodsSold,
            HasCost = sale.CostOfGoodsSold.HasValue
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

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static DateRange ResolveRange(ReportingFilterDto filter) =>
        ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);

    private static long? MachineId(ReportingFilterDto filter) => filter.MachineId ?? filter.MachineID;

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
        public bool HasCost { get; set; }
        public decimal? CostOfGoodsSold { get; set; }
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
