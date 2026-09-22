using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Bookkeeping;

namespace Inventory.Application.Reporting.Bookkeeping;

/// <summary>
/// The bookkeeping report use case: resolves the requested date range/machine scope, retrieves
/// report facts through the narrow <see cref="IBookkeepingReportFactsProvider"/> port, applies the
/// Domain profit/margin/GST policy, and builds the authoritative <see cref="BookkeepingReportDto"/>
/// consumed by both the API response and the CSV/XLSX export.
/// </summary>
public sealed class GetBookkeepingReport : IGetBookkeepingReport
{
    private readonly IBookkeepingReportFactsProvider _facts;

    public GetBookkeepingReport(IBookkeepingReportFactsProvider facts)
    {
        _facts = facts;
    }

    public async Task<BookkeepingReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var range = ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);
        var machineId = filter.MachineId ?? filter.MachineID;
        var isMachineFiltered = machineId.HasValue;

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, machineId, cancellationToken);

        var profit = BookkeepingProfitPolicy.Calculate(new BookkeepingProfitInputs(
            Sales: facts.GrossSales,
            PartialCostOfGoods: facts.PartialCostOfGoods,
            IsCogsComplete: facts.IsCogsComplete,
            CardSales: facts.CardSales,
            FeesIncludingGst: facts.ProcessingFees.TotalFeeIncGst,
            HasMissingFeeRates: facts.ProcessingFees.HasMissingRates,
            IsMachineFiltered: isMachineFiltered,
            CommissionCompleteForScope: facts.CommissionCompleteForScope,
            SiteCommission: facts.SiteCommission,
            ReceiptCosts: facts.ReceiptDeliveryCost + facts.ReceiptPackageCost,
            OperatingExpensesTotal: facts.OperatingExpensesTotal,
            ImportedHasNetSettlement: facts.ImportedHasNetSettlement,
            ImportedNetSettlement: facts.ImportedNetSettlement));

        var qualityNotes = new List<string>();
        if (facts.UnknownTransactions > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        if (isMachineFiltered && !facts.ImportedMachineFilterMatched)
            qualityNotes.Add("No reimbursement device row matched the selected machine ID.");
        if (facts.ImportedFeesMachineFilterLimited)
            qualityNotes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        if (facts.ProcessingFees.HasMissingRates)
            qualityNotes.Add($"{facts.ProcessingFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; profit is unavailable.");
        if (!facts.IsCogsComplete)
            qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");
        if (isMachineFiltered)
            qualityNotes.Add("Net profit is unavailable for a machine-filtered report because shared business overhead is not allocated to individual machines.");
        if (!facts.CommissionIsComplete)
            qualityNotes.Add($"Commission configuration is incomplete; {(isMachineFiltered ? "direct profit" : "net profit")} is unavailable.");
        qualityNotes.AddRange(facts.CommissionWarnings);

        var quality = ReportingQuality.Quality(
            missingStatus: true,
            historicalCostUnavailable: !facts.IsCogsComplete,
            gstClassificationMissing: !facts.ImportedContainsGstClassification,
            commissionNotPersisted: !facts.CommissionIsComplete,
            containsUnmappedProducts: false,
            notes: qualityNotes);

        var fees = facts.ProcessingFees.TotalFeeExGst;
        var feesIncludingGst = facts.ProcessingFees.TotalFeeIncGst;

        return new BookkeepingReportDto(range.From, range.ToDate, AustralianFyHelper.Label(range.From),
            facts.GrossSales, facts.IsCogsComplete ? facts.PartialCostOfGoods : null, profit.GrossProfit,
            fees, profit.NetSettlement, profit.GstOnSales, facts.ProcessingFees.TotalFeeGst, quality,
            facts.SiteCommission, profit.NetProfit, profit.NetMarginPercent,
            fees, feesIncludingGst, facts.ReceiptDeliveryCost, facts.ReceiptPackageCost, facts.OperatingExpensesTotal,
            facts.CardSales, facts.CashSales, facts.CardTransactions, facts.CashTransactions,
            ReportingCalculations.PercentageOf(feesIncludingGst, facts.CardSales))
        {
            PartialCostOfGoods = facts.PartialCostOfGoods,
            IsCogsComplete = facts.IsCogsComplete,
            UncostedTransactionCount = facts.UncostedTransactionCount,
            UncostedSalesAmount = facts.UncostedSalesAmount,
            StructuredOperatingExpenses = facts.OperatingExpensesTotal,
            OperatingExpenseGst = facts.OperatingExpensesGst,
            OperatingExpensesByCategory = facts.OperatingExpensesByCategory,
            DirectProfit = profit.DirectProfit,
            DirectMarginPercent = profit.DirectMarginPercent,
            NayaxProcessingFees = facts.ProcessingFees
        };
    }
}
