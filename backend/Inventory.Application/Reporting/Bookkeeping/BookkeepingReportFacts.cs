using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Gst;

namespace Inventory.Application.Reporting.Bookkeeping;

/// <summary>
/// Raw, already-aggregated facts needed to build the bookkeeping report for a resolved date range
/// and optional machine filter. Contains no financial formulas: those live in
/// <c>Inventory.Domain.Reporting.Bookkeeping</c> and in this feature's use case.
/// </summary>
public sealed record BookkeepingReportFacts(
    decimal GrossSales,
    // The same completed sales as GrossSales, aggregated by the GST rule stored on the product each
    // one is attributed to (issue #1255). It is the raw input to SalesGstPolicy, which decides which
    // rules are taxable; an adapter groups rows and decides nothing. The groups add up to GrossSales
    // by construction, because they are summed from the same rows, and a group whose rule is null is
    // one whose product could not be resolved.
    IReadOnlyList<SalesByGstRule> SalesByProductGstRule,
    decimal CardSales,
    int CardTransactions,
    decimal CashSales,
    int CashTransactions,
    int UnknownTransactions,
    // Completed (status 12) sales in the requested business, date range and machine scope - the
    // same rows every amount above is derived from, counted once here rather than re-counted per
    // payment method or re-queried by another report (issue #804). It is deliberately not derived
    // from an amount: a period of refunded-to-zero sales still had transactions.
    int TransactionCount,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    int UncostedTransactionCount,
    decimal UncostedSalesAmount,
    decimal ReceiptDeliveryCost,
    decimal ReceiptPackageCost,
    // The actual reimbursement for the range and whether there is one at all. Since issue #1253
    // these count only the imported payouts whose own declared sales period lies *wholly inside*
    // the requested range: a payout the range merely overlaps settles days outside it, so its whole
    // total is not this range's money. ImportedHasNetSettlement is false - "Not available" - when no
    // such payout states a total, which is never the same statement as a settlement of zero.
    bool ImportedHasNetSettlement,
    decimal ImportedNetSettlement,
    bool ImportedContainsRows,
    bool ImportedContainsGstClassification,
    bool ImportedMachineFilterMatched,
    bool ImportedFeesMachineFilterLimited,
    decimal OperatingExpensesTotal,
    decimal OperatingExpensesGst,
    IReadOnlyDictionary<string, decimal> OperatingExpensesByCategory,
    decimal SiteCommission,
    // Scoped to the selected machine when a machine filter is active; otherwise whole-business.
    // Used only to gate direct/net profit completeness.
    bool CommissionCompleteForScope,
    // The commission service's own overall completeness flag, independent of CommissionCompleteForScope.
    // Used only for the human-readable "commission configuration is incomplete" data-quality note.
    bool CommissionIsComplete,
    IReadOnlyList<string> CommissionWarnings,
    NayaxProcessingFeeResult ProcessingFees,
    // Transaction-status diagnostics for the requested business, date range and machine scope,
    // counted before the completed-sale filter excludes them (issue #476), so the report can say
    // what was actually excluded and why. They never change which sales the financial totals
    // include: only status 12 is a completed sale, and every amount above is still derived from
    // completed sales alone.
    //
    // The kinds stay separate because they mean different things. Pending, refunded and
    // cancelled/declined rows are normal Nayax outcomes, not data errors.
    // UnknownStatusTransactionCount counts rows whose status ID is present but unrecognised, and
    // MissingStatusTransactionCount counts rows carrying no status ID at all; the two never
    // overlap, and only those two are data-quality problems.
    int PendingTransactionCount,
    int RefundedTransactionCount,
    int DeclinedOrCancelledTransactionCount,
    int UnknownStatusTransactionCount,
    int MissingStatusTransactionCount)
{
    /// <summary>
    /// Status-12 queue-delivered sales in scope that issue #1105's review keeps out of every figure:
    /// contradicted by the business's own Nayax <c>lastSales</c>, or excluded by the Owner.
    /// </summary>
    public int ExcludedByReviewTransactionCount { get; init; }

    /// <summary>
    /// The imported payouts whose declared period lies wholly inside the requested range - the ones
    /// <see cref="ImportedNetSettlement"/> is the total of - with the period dates each settles
    /// (issue #1253), so the report can label the actual reimbursement with what it is made of.
    /// </summary>
    public IReadOnlyList<ImportedReimbursementPeriodDto> ImportedFullyCoveredPeriods { get; init; } = [];

    /// <summary>
    /// The imported payouts the requested range only <em>partly</em> overlaps. They are reported on
    /// their own so a reader can see which payout period the range cut, and they are part of no
    /// total: neither the actual reimbursement nor the net settlement includes them.
    /// </summary>
    public IReadOnlyList<ImportedReimbursementPeriodDto> ImportedPartlyCoveredPeriods { get; init; } = [];

    /// <summary>
    /// The coverage evidence this period rests on (issue #730), in the one carrier every other
    /// sales-based report already uses. It travels with the UTC window the adapter actually selected
    /// its sales over - business-day midnights since issue #1250 - because a coverage range may only
    /// be compared with the window the figures were measured over, and the requested calendar dates
    /// are not that window.
    ///
    /// <para>Whether the scope holds any stored provider sale at all
    /// (<see cref="ReportCoverageFacts.ContainsSalesRows"/>) is counted before the completed-sale
    /// filter and is deliberately not derived from <see cref="GrossSales"/>: a period holding only
    /// cancelled rows has sales data and a gross sales total of zero.</para>
    /// </summary>
    public ReportCoverageFacts Coverage { get; init; } = new();
}
