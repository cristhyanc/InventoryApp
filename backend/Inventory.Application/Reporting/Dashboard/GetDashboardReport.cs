using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Dashboard;

namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// The dashboard report use case: reuses the already-migrated bookkeeping and product
/// profitability reports through <see cref="IGetBookkeepingReport"/>/
/// <see cref="IGetProductProfitabilityReport"/> for every sales/profit/fee/commission/operating
/// expense figure they already compute (rather than re-deriving profit or product figures a second
/// way), retrieves the narrow set of facts unique to the dashboard summary through
/// <see cref="IDashboardReportFactsProvider"/>, applies the Domain
/// <see cref="DashboardReimbursementPolicy"/> for the expected-versus-actual reimbursement
/// figures/status, and builds the authoritative <see cref="DashboardReportDto"/> consumed by both
/// the API response and the CSV/XLSX export.
/// </summary>
public sealed class GetDashboardReport
{
    private readonly IGetBookkeepingReport _getBookkeepingReport;
    private readonly IGetProductProfitabilityReport _getProductProfitabilityReport;
    private readonly IDashboardReportFactsProvider _facts;

    public GetDashboardReport(IGetBookkeepingReport getBookkeepingReport,
        IGetProductProfitabilityReport getProductProfitabilityReport, IDashboardReportFactsProvider facts)
    {
        _getBookkeepingReport = getBookkeepingReport;
        _getProductProfitabilityReport = getProductProfitabilityReport;
        _facts = facts;
    }

    public async Task<DashboardReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var machineId = filter.MachineId ?? filter.MachineID;
        var isMachineFiltered = machineId.HasValue;

        var bookkeeping = await _getBookkeepingReport.Handle(filter, cancellationToken);
        var productReport = await _getProductProfitabilityReport.Handle(filter, cancellationToken);
        var facts = await _facts.GetFactsAsync(bookkeeping.From, bookkeeping.To, machineId, cancellationToken);

        var reimbursement = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: bookkeeping.CardSales,
            FeesIncludingGst: bookkeeping.NayaxFeesIncludingGst,
            ActualReimbursement: facts.ImportedNetSettlement,
            ImportedContainsRows: facts.ImportedContainsRows));

        var qualityNotes = new List<string>(productReport.DataQuality.Notes ?? []);
        if (!bookkeeping.IsCogsComplete)
            qualityNotes.Add("One or more completed sales have no persisted COGS; profitability is unavailable.");
        if (bookkeeping.NayaxProcessingFees.HasMissingRates)
            qualityNotes.Add($"{bookkeeping.NayaxProcessingFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; profit is unavailable.");
        if (isMachineFiltered)
            qualityNotes.Add("Net profit is unavailable for a machine-filtered report because shared business overhead is not allocated to individual machines.");
        if (!facts.CommissionIsComplete)
            qualityNotes.Add($"Commission configuration is incomplete; {(isMachineFiltered ? "direct profit" : "net profit")} is unavailable.");
        qualityNotes.AddRange(facts.CommissionWarnings);

        var quality = ReportingQuality.Quality(
            missingStatus: true,
            historicalCostUnavailable: !bookkeeping.IsCogsComplete,
            gstClassificationMissing: bookkeeping.DataQuality.GstClassificationMissing,
            commissionNotPersisted: !facts.CommissionIsComplete,
            containsUnmappedProducts: productReport.DataQuality.ContainsUnmappedProducts,
            notes: qualityNotes);

        var grossMarginPercent = bookkeeping.GrossProfit.HasValue
            ? ReportingCalculations.MarginPercent(bookkeeping.Sales, bookkeeping.PartialCostOfGoods)
            : (decimal?)null;

        return new DashboardReportDto(bookkeeping.From, bookkeeping.To, bookkeeping.Sales, bookkeeping.GrossProfit,
            facts.TransactionCount, facts.TransactionCount, facts.MachineCount, facts.ProductCount,
            productReport.Rows.Count(x => x.IsUnmapped), quality, bookkeeping.NayaxFeesIncludingGst,
            reimbursement.ActualReimbursement, bookkeeping.SiteCommission, bookkeeping.NetProfit, bookkeeping.NetMarginPercent,
            bookkeeping.NayaxFeesExGst, bookkeeping.DeliveryCosts, bookkeeping.PackageCosts, bookkeeping.OtherOperatingExpenses,
            bookkeeping.CardSales, bookkeeping.CashSales, bookkeeping.CardTransactionCount, bookkeeping.CashTransactionCount)
        {
            TotalSales = bookkeeping.Sales,
            CostOfGoodsSold = bookkeeping.CostOfGoods,
            PartialCostOfGoods = bookkeeping.PartialCostOfGoods,
            IsCogsComplete = bookkeeping.IsCogsComplete,
            UncostedTransactionCount = bookkeeping.UncostedTransactionCount,
            UncostedSalesAmount = bookkeeping.UncostedSalesAmount,
            AverageSale = ReportingCalculations.Average(bookkeeping.Sales, facts.TransactionCount),
            GrossMarginPercent = grossMarginPercent,
            NayaxFeesIncludingGst = bookkeeping.NayaxFeesIncludingGst,
            OtherOperatingExpenses = bookkeeping.OtherOperatingExpenses,
            ExpectedReimbursement = reimbursement.ExpectedReimbursement,
            ActualReimbursement = reimbursement.ActualReimbursement,
            ReimbursementDifference = reimbursement.Difference,
            IsReconciled = reimbursement.IsReconciled,
            ReconciliationStatus = reimbursement.Status,
            ReconciliationTolerance = DashboardReimbursementPolicy.Tolerance,
            AdjustmentsSupported = false,
            DirectProfit = bookkeeping.DirectProfit,
            DirectMarginPercent = bookkeeping.DirectMarginPercent,
            StructuredOperatingExpenses = bookkeeping.StructuredOperatingExpenses,
            OperatingExpenseGst = bookkeeping.OperatingExpenseGst,
            NayaxProcessingFees = bookkeeping.NayaxProcessingFees
        };
    }
}
