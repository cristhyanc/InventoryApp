using System.Globalization;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Bookkeeping;
using Inventory.Domain.Reporting.Dashboard;
using Inventory.Domain.Reporting.Gst;

namespace Inventory.Application.Reporting.Bookkeeping;

/// <summary>
/// The bookkeeping report use case: resolves the requested date range/machine scope, retrieves
/// report facts through the narrow <see cref="IBookkeepingReportFactsProvider"/> port, applies the
/// Domain profit/margin/GST policy and the Domain
/// <see cref="DashboardReimbursementPolicy"/> that decides the expected-versus-actual reimbursement
/// comparison (issue #805), and builds the authoritative <see cref="BookkeepingReportDto"/>
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

        // GST on sales, from each sold product's own GST rule (issue #1255). Only an explicitly
        // GST-free product reduces it; a product with no rule and a sale whose product could not be
        // resolved stay taxable, and the sales that rest on that default are reported separately so
        // the figure is not silently built on it. The Domain policy is the one place that decides
        // which rules are taxable and the one place the eleventh is taken, once, from the period's
        // taxable total.
        var salesGst = SalesGstPolicy.Calculate(facts.SalesByProductGstRule);

        // The expected-versus-actual reimbursement comparison (issue #805), from the same imported
        // settlement facts this report already reads for its fees and net settlement. The policy is
        // the Domain one the reporting dashboard introduced and still publishes - named for that
        // feature, but a deterministic rule over already-aggregated facts, so this report reuses it
        // rather than writing a second tolerance or status rule. The dependency direction is
        // unchanged: the dashboard composes this report, and this report does not know it exists.
        //
        // Since issue #1253 only a payout whose own period lies wholly inside the requested range
        // is an actual reimbursement for it, so the actual is absent rather than zero for a range
        // that merely cuts a payout period - and the policy then reports what happened instead of a
        // difference nobody can act on.
        var reimbursement = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: facts.CardSales,
            FeesIncludingGst: facts.ProcessingFees.TotalFeeIncGst,
            ActualReimbursement: facts.ImportedHasNetSettlement ? facts.ImportedNetSettlement : null,
            HasPartlyCoveredPayout: facts.ImportedPartlyCoveredPeriods.Count > 0));

        var qualityNotes = new List<string>();
        if (facts.MissingStatusTransactionCount > 0)
            qualityNotes.Add($"{facts.MissingStatusTransactionCount} Nayax transaction(s) have no status ID and are excluded from completed sales.");
        if (facts.UnknownStatusTransactionCount > 0)
            qualityNotes.Add($"{facts.UnknownStatusTransactionCount} Nayax transaction(s) have an unrecognised status ID and are excluded from completed sales.");
        if (facts.ExcludedByReviewTransactionCount > 0)
            qualityNotes.Add($"{facts.ExcludedByReviewTransactionCount} Nayax sale(s) contradicted by Nayax or excluded by the owner are not counted in completed sales.");
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
        //
        // The decision is made over the window the facts provider actually selected its rows by,
        // which it carries on the coverage record - business-day midnights since issue #1250, not
        // the requested calendar dates a business day straddles. Judging coverage against a window
        // the figures were never measured over is what this carrier exists to prevent, and it is how
        // the daily, reconciliation and profitability reports already decide these states.
        var availability = facts.Coverage.Availability();
        ReportAvailabilityNotes.Add(qualityNotes, availability.Sales, SalesFigures, ReportAvailabilityNotes.NoSalesData);
        ReportAvailabilityNotes.Add(qualityNotes, availability.Fees, FeeFigures, ReportAvailabilityNotes.NoSettlementData);

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
            fees, profit.NetSettlement, salesGst.GstOnSales, facts.ProcessingFees.TotalFeeGst, quality,
            facts.SiteCommission, profit.NetProfit, profit.NetMarginPercent,
            fees, feesIncludingGst, facts.ReceiptDeliveryCost, facts.ReceiptPackageCost, facts.OperatingExpensesTotal,
            facts.CardSales, facts.CashSales, facts.CardTransactions, facts.CashTransactions,
            ReportingCalculations.PercentageOf(feesIncludingGst, facts.CardSales),
            facts.PendingTransactionCount, facts.RefundedTransactionCount,
            facts.DeclinedOrCancelledTransactionCount, facts.UnknownStatusTransactionCount,
            facts.MissingStatusTransactionCount)
        {
            // Sales volume and the average sale over it (issue #804), from the same completed sales
            // every amount above is derived from, through the shared average formula the reporting
            // dashboard already used - which answers 0 for a period with no completed transaction
            // rather than dividing by zero. Both are provider-sourced and carry the sales state.
            TransactionCount = facts.TransactionCount,
            AverageSale = ReportingCalculations.Average(facts.GrossSales, facts.TransactionCount),
            // The sales behind GstOnSales, so a reader can see which part of gross sales it was
            // taken from (issue #1255). TaxableSales and GstFreeSales add up to Sales, and
            // NoGstRuleSales is the part of TaxableSales that is taxable only because nobody has
            // given those products a rule.
            TaxableSales = salesGst.TaxableSales,
            GstFreeSales = salesGst.GstFreeSales,
            NoGstRuleSales = salesGst.NoGstRuleSales,
            // The calculated expectation, the imported actual and the signed difference between
            // them (issue #805). NetSettlement above keeps its own established meaning and is not
            // one of these: it prefers the imported amount when the period has one.
            ExpectedReimbursement = reimbursement.ExpectedReimbursement,
            ActualReimbursement = reimbursement.ActualReimbursement,
            ReimbursementDifference = reimbursement.Difference,
            IsReconciled = reimbursement.IsReconciled,
            ReconciliationStatus = reimbursement.Status,
            ReconciliationTolerance = DashboardReimbursementPolicy.Tolerance,
            // What the actual reimbursement is made of, and what the range cut (issue #1253). The
            // periods are published rather than described so the presentation can label the figure
            // with the dates it settles, and the partly covered payouts are listed beside it
            // without being part of any total here.
            ReimbursementPeriods = facts.ImportedFullyCoveredPeriods,
            PartlyCoveredReimbursements = facts.ImportedPartlyCoveredPeriods,
            // Whether the net settlement above is this report's own estimate - card sales less fees -
            // because no payout covering these dates in full recorded one.
            IsNetSettlementEstimated = profit.IsNetSettlementEstimated,
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
            ProviderAvailability = availability
        };
    }

    /// <summary>
    /// The sales-sourced figures this report shows, as <see cref="ReportAvailabilityNotes"/> names
    /// them. Public because the dashboard report republishes this report's decided states beside the
    /// same figures and must name them the same way rather than inventing a second wording.
    /// </summary>
    public const string SalesFigures =
        "Sales, the card/cash split, the transaction count and average sale, cost of goods and profit";

    /// <summary>The settlement-sourced figures it shows, for the same reason.</summary>
    public const string FeeFigures =
        "Nayax processing fees, the GST on them, the imported actual reimbursement and the reimbursement reconciliation";
}
