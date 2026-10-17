using System.Globalization;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Availability;
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
        if (facts.MissingStatusTransactionCount > 0)
            qualityNotes.Add($"{facts.MissingStatusTransactionCount} Nayax transaction(s) have no status ID and are excluded from completed sales.");
        if (facts.UnknownStatusTransactionCount > 0)
            qualityNotes.Add($"{facts.UnknownStatusTransactionCount} Nayax transaction(s) have an unrecognised status ID and are excluded from completed sales.");
        if (facts.UnknownTransactions > 0)
            qualityNotes.Add("One or more transactions have an unknown payment method.");
        if (isMachineFiltered && !facts.ImportedMachineFilterMatched)
            qualityNotes.Add("No reimbursement device row matched the selected machine ID.");
        if (facts.ImportedFeesMachineFilterLimited)
            qualityNotes.Add("Imported fees are account-level amounts and are not allocated to a selected machine.");
        if (facts.ProcessingFees.HasMissingRates)
            qualityNotes.Add($"{facts.ProcessingFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; profit is unavailable.");
        if (!facts.IsCogsComplete)
            qualityNotes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{facts.UncostedTransactionCount} completed sale(s) totalling {facts.UncostedSalesAmount:0.00} have no persisted COGS; profit is incomplete."));
        if (isMachineFiltered)
            qualityNotes.Add("Net profit is unavailable for a machine-filtered report because shared business overhead is not allocated to individual machines.");
        if (!facts.CommissionIsComplete)
            qualityNotes.Add($"Commission configuration is incomplete; {(isMachineFiltered ? "direct profit" : "net profit")} is unavailable.");
        qualityNotes.AddRange(facts.CommissionWarnings);

        // How well recorded provider coverage accounts for this period (issue #730). The two
        // families are decided separately, from the sources that actually feed them: sale instants
        // for the sales figures, imported reimbursement periods for the fee figures. A settlement
        // file arriving never makes the sales figures complete, and vice versa.
        var salesAvailability = ReportAvailabilityPolicy.Decide(
            range.From, range.EndExclusive, facts.SalesCoverage, facts.ContainsSalesRows);
        var feesAvailability = ReportAvailabilityPolicy.Decide(
            range.From, range.EndExclusive, facts.SettlementCoverage, facts.ImportedContainsRows);
        AddAvailabilityNote(qualityNotes, salesAvailability, SalesFigures, "no sales data is recorded for it");
        AddAvailabilityNote(qualityNotes, feesAvailability, FeeFigures, "no settlement data is imported for it");

        // Conditional, scope-derived diagnostics (issue #476): every note above comes from a fact in
        // the requested business, date range and machine scope, and the calculation methodology this
        // report used lives in its "How this report is calculated" help instead. An empty note list
        // means no problem was detected in this period - never that GST classification is verified.
        var quality = ReportingQuality.Conditional(
            missingStatus: facts.MissingStatusTransactionCount > 0 || facts.UnknownStatusTransactionCount > 0,
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
            ReportingCalculations.PercentageOf(feesIncludingGst, facts.CardSales),
            facts.PendingTransactionCount, facts.RefundedTransactionCount,
            facts.DeclinedOrCancelledTransactionCount, facts.UnknownStatusTransactionCount,
            facts.MissingStatusTransactionCount)
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
            NayaxProcessingFees = facts.ProcessingFees,
            ProviderAvailability = ReportProviderAvailabilityDto.From(salesAvailability, feesAvailability)
        };
    }

    private const string SalesFigures = "Sales, the card/cash split, cost of goods and profit";
    private const string FeeFigures = "Nayax processing fees and the GST on them";

    /// <summary>
    /// The one wording of each non-complete availability state, shared by the figure families so a
    /// reader is told the same thing about a gap wherever it is (issue #730). A complete period adds
    /// nothing: an empty note list means no problem was detected, exactly as issue #476 established.
    ///
    /// The stale note prints a UTC instant rather than a business date on purpose. It is the moment
    /// coverage reaches, not a business day, and the business reporting timezone governs which day a
    /// sale belongs to - not how an "as of" instant is spelled (AGENTS.md § Reporting dates and
    /// filters).
    /// </summary>
    private static void AddAvailabilityNote(
        List<string> notes, ReportAvailability availability, string figures, string missingReason)
    {
        switch (availability.State)
        {
            case ReportAvailabilityState.Missing:
                notes.Add($"{figures} are unavailable for this period: {missingReason}, and no synchronization or import covered it. The figures shown are not a measured zero.");
                break;
            case ReportAvailabilityState.Stale:
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{figures} are accounted for only as of {availability.AsOfUtc:yyyy-MM-dd HH:mm} UTC; the rest of this period has not been covered by a successful synchronization or import."));
                break;
            case ReportAvailabilityState.Unverified:
                notes.Add($"{figures} are not accounted for across the whole period: the data predates coverage recording, or a failed or partial run left a gap. The amounts shown may be incomplete.");
                break;
            case ReportAvailabilityState.Complete:
            default:
                break;
        }
    }
}
