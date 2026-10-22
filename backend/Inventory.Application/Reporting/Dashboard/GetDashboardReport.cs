using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;

namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// The dashboard report use case: reuses the already-migrated bookkeeping and product
/// profitability reports through <see cref="IGetBookkeepingReport"/>/
/// <see cref="IGetProductProfitabilityReport"/> for every sales/profit/fee/commission/operating
/// expense figure they already compute (rather than re-deriving profit or product figures a second
/// way), retrieves the narrow set of facts unique to the dashboard summary through
/// <see cref="IDashboardReportFactsProvider"/>, and builds the authoritative
/// <see cref="DashboardReportDto"/> consumed by both the API response and the CSV/XLSX export.
///
/// <para>The expected-versus-actual reimbursement figures and status were decided here, by the
/// Domain <c>DashboardReimbursementPolicy</c>, until issue #805 moved that comparison into the
/// bookkeeping report this summary already composes. It now republishes those figures for the same
/// reason it republishes the transaction count (issue #804): one period cannot have two
/// reconciliation answers.</para>
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

        // The bookkeeping report already decided how well recorded provider coverage accounts for
        // this period (issue #730); this report composes that one for every provider figure it
        // shows, so it republishes those states rather than deciding them a second time (issue
        // #731). The notes are named for this report's own cards.
        var availability = bookkeeping.ProviderAvailability;
        ReportAvailabilityNotes.AddBoth(qualityNotes, availability, SalesFigures, FeeFigures);

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

        // The completed-transaction count is the bookkeeping report's own since issue #804, which
        // relocated it there rather than letting two reports count the same rows separately. This
        // summary reports it, and the average sale over it, exactly as that report decided them.
        // Issue #805 did the same for the expected-versus-actual reimbursement comparison below.
        return new DashboardReportDto(bookkeeping.From, bookkeeping.To, bookkeeping.Sales, bookkeeping.GrossProfit,
            bookkeeping.TransactionCount, bookkeeping.TransactionCount, facts.MachineCount, facts.ProductCount,
            productReport.Rows.Count(x => x.IsUnmapped), quality, bookkeeping.NayaxFeesIncludingGst,
            bookkeeping.ActualReimbursement, bookkeeping.SiteCommission, bookkeeping.NetProfit, bookkeeping.NetMarginPercent,
            bookkeeping.NayaxFeesExGst, bookkeeping.DeliveryCosts, bookkeeping.PackageCosts, bookkeeping.OtherOperatingExpenses,
            bookkeeping.CardSales, bookkeeping.CashSales, bookkeeping.CardTransactionCount, bookkeeping.CashTransactionCount)
        {
            TotalSales = bookkeeping.Sales,
            CostOfGoodsSold = bookkeeping.CostOfGoods,
            PartialCostOfGoods = bookkeeping.PartialCostOfGoods,
            IsCogsComplete = bookkeeping.IsCogsComplete,
            UncostedTransactionCount = bookkeeping.UncostedTransactionCount,
            UncostedSalesAmount = bookkeeping.UncostedSalesAmount,
            AverageSale = bookkeeping.AverageSale,
            GrossMarginPercent = grossMarginPercent,
            NayaxFeesIncludingGst = bookkeeping.NayaxFeesIncludingGst,
            OtherOperatingExpenses = bookkeeping.OtherOperatingExpenses,
            ExpectedReimbursement = bookkeeping.ExpectedReimbursement,
            ActualReimbursement = bookkeeping.ActualReimbursement,
            ReimbursementDifference = bookkeeping.ReimbursementDifference,
            IsReconciled = bookkeeping.IsReconciled,
            ReconciliationStatus = bookkeeping.ReconciliationStatus,
            ReconciliationTolerance = bookkeeping.ReconciliationTolerance,
            AdjustmentsSupported = false,
            DirectProfit = bookkeeping.DirectProfit,
            DirectMarginPercent = bookkeeping.DirectMarginPercent,
            StructuredOperatingExpenses = bookkeeping.StructuredOperatingExpenses,
            OperatingExpenseGst = bookkeeping.OperatingExpenseGst,
            NayaxProcessingFees = bookkeeping.NayaxProcessingFees,
            ProviderAvailability = availability
        };
    }

    /// <summary>The sales-sourced figures this summary shows, named for its own cards.</summary>
    public const string SalesFigures =
        "Sales, the card/cash split, quantity, cost of goods, gross profit and site commission";

    /// <summary>The settlement-sourced figures it shows.</summary>
    public const string FeeFigures =
        "Nayax fees, the actual reimbursement and the reimbursement reconciliation";
}
