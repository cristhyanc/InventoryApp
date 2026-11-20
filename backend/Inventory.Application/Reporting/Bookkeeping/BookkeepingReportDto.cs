using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Dashboard;

namespace Inventory.Application.Reporting.Bookkeeping;

public record BookkeepingReportDto(
    DateTime From,
    DateTime To,
    string FinancialYear,
    decimal Sales,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    decimal Fees,
    decimal NetSettlement,
    decimal GstOnSales,
    decimal GstOnFees,
    ReportingDataQualityDto DataQuality,
    decimal SiteCommission = 0m,
    decimal? NetProfit = null,
    decimal? NetMarginPercent = null,
    decimal NayaxFeesExGst = 0m,
    decimal NayaxFeesIncludingGst = 0m,
    decimal DeliveryCosts = 0m,
    decimal PackageCosts = 0m,
    decimal OtherOperatingExpenses = 0m,
    decimal CardSales = 0m,
    decimal CashSales = 0m,
    int CardTransactionCount = 0,
    int CashTransactionCount = 0,
    decimal NayaxProcessingRate = 0m,
    int PendingTransactionCount = 0,
    int RefundedTransactionCount = 0,
    int DeclinedOrCancelledTransactionCount = 0,
    int UnknownStatusTransactionCount = 0,
    int MissingStatusTransactionCount = 0)
{
    /// <summary>
    /// Completed (status 12) sales in this period and machine scope - the volume behind
    /// <see cref="Sales"/>, and the denominator of <see cref="AverageSale"/> (issue #804). It is a
    /// provider-sourced figure and is governed by <see cref="ProviderAvailability"/>'s sales state,
    /// exactly as the card/cash transaction counts beside it are.
    /// </summary>
    public int TransactionCount { get; init; }

    /// <summary>
    /// <see cref="Sales"/> over <see cref="TransactionCount"/>, through the shared
    /// <c>ReportingCalculations.Average</c> - the one average-sale formula, which answers <c>0</c>
    /// for a period with no completed transaction rather than dividing by zero. A period whose
    /// sales state is not <c>Complete</c> shows that state instead of this zero, because nothing
    /// measured it.
    /// </summary>
    public decimal AverageSale { get; init; }

    /// <summary>
    /// The GST-inclusive sales <see cref="GstOnSales"/> was taken from: every completed sale in
    /// scope except those of products whose GST rule is explicitly GST-free (issue #1255). A product
    /// with no rule at all, and a sale whose product could not be resolved, are taxable and are in
    /// here. It is a provider-sourced figure and carries
    /// <see cref="ReportProviderAvailabilityDto.Sales"/>, like <see cref="Sales"/> itself.
    /// </summary>
    public decimal TaxableSales { get; init; }

    /// <summary>
    /// Sales of products whose GST rule is explicitly GST-free, which contribute no GST at all.
    /// <see cref="TaxableSales"/> plus this is <see cref="Sales"/>.
    /// </summary>
    public decimal GstFreeSales { get; init; }

    /// <summary>
    /// The part of <see cref="TaxableSales"/> that is taxable only because nobody has given those
    /// products a GST rule - the default every product starts at - plus any sale whose product could
    /// not be resolved. It is reported on its own so the GST figure never rests invisibly on that
    /// default: setting those products' rules is what makes it fall.
    /// </summary>
    public decimal NoGstRuleSales { get; init; }

    /// <summary>
    /// The reimbursement this period's card sales and Nayax fees say to expect - card sales less
    /// fees including GST, calculated by <c>DashboardReimbursementPolicy</c> (issue #805). It is a
    /// calculation, never an imported amount, and it is deliberately not
    /// <see cref="NetSettlement"/>, which prefers the imported figure for the period when there is
    /// one. Governed by <see cref="ReportProviderAvailabilityDto.Combined"/>, because it is built
    /// from a sales figure and a fee figure.
    /// </summary>
    public decimal ExpectedReimbursement { get; init; }

    /// <summary>
    /// The net reimbursement the settlement import recorded for this period: the total of the
    /// payouts whose own declared sales period lies <em>wholly inside</em> the requested range
    /// (issue #1253). A payout the range only overlaps settles days outside it, so it is reported in
    /// <see cref="PartlyCoveredReimbursements"/> and is no part of this figure.
    ///
    /// <para>It is <c>null</c> - "Not available" - when no payout lies wholly inside the range, and
    /// is <strong>never</strong> <c>0</c> for that reason: a settlement import that really settled
    /// nothing is a measured zero in a <c>Complete</c> period, and the two must never be read as
    /// each other. <see cref="ReconciliationStatus"/> says which case a range is in and
    /// <see cref="ReportProviderAvailabilityDto.Fees"/> labels the figure.</para>
    /// </summary>
    public decimal? ActualReimbursement { get; init; }

    /// <summary>
    /// <see cref="ActualReimbursement"/> less <see cref="ExpectedReimbursement"/>, signed: negative
    /// means the import paid less than this period's card sales and fees expected. <c>null</c>
    /// whenever there is no actual reimbursement to compare, because a difference computed against a
    /// missing side is the most misleading figure in this comparison. Governed by
    /// <see cref="ReportProviderAvailabilityDto.Combined"/>.
    /// </summary>
    public decimal? ReimbursementDifference { get; init; }

    /// <summary>
    /// Whether the comparison reconciled: a payout lying wholly inside the range recorded an actual
    /// reimbursement and the absolute difference is within <see cref="ReconciliationTolerance"/>. A
    /// range with no such payout is never reconciled and is never flagged for review either -
    /// nothing was compared.
    /// </summary>
    public bool IsReconciled { get; init; }

    /// <summary>
    /// <c>Pending</c> (no imported payout relates to the range at all), <c>Range cuts a payout
    /// period</c> (every payout for these dates settles days outside the range, so nothing was
    /// compared), <c>Reconciled</c> or <c>Needs Review</c> - from the one Domain policy, which is
    /// also where these literals are named.
    /// </summary>
    public string ReconciliationStatus { get; init; } = PendingReconciliationStatus;

    /// <summary>
    /// The tolerance the status was decided with, reported so no presentation or export restates the
    /// repository's `$0.01` rule itself.
    /// </summary>
    public decimal ReconciliationTolerance { get; init; } = DashboardReimbursementPolicy.Tolerance;

    /// <summary>
    /// The imported payouts <see cref="ActualReimbursement"/> is the total of, each with the sales
    /// period it settles, so the figure can be labelled with the dates it covers (issue #1253).
    /// Empty whenever the actual reimbursement is not available.
    /// </summary>
    public IReadOnlyList<ImportedReimbursementPeriodDto> ReimbursementPeriods { get; init; } = [];

    /// <summary>
    /// The imported payouts this range only <em>partly</em> overlaps, with their own period dates
    /// and totals (issue #1253). They are published so the report can show which payout period the
    /// requested range cut, and they are part of no total in this response - not
    /// <see cref="ActualReimbursement"/>, not <see cref="NetSettlement"/> and not
    /// <see cref="ReimbursementDifference"/>.
    /// </summary>
    public IReadOnlyList<ImportedReimbursementPeriodDto> PartlyCoveredReimbursements { get; init; } = [];

    /// <summary>
    /// Whether <see cref="NetSettlement"/> is this report's own estimate - this period's card sales
    /// less its Nayax fees including GST - because no payout covering these dates in full recorded
    /// one (issue #1253). An estimate is always labelled as one: it is a calculation from the
    /// figures here, not an amount anybody paid.
    /// </summary>
    public bool IsNetSettlementEstimated { get; init; }

    /// <summary>
    /// The status a report with no imported settlement row carries, named so a presentation can
    /// recognise it without repeating the literal (the dashboard's own card does the same). It is
    /// the Domain policy's own constant: the vocabulary is decided in one place.
    /// </summary>
    public const string PendingReconciliationStatus = DashboardReimbursementPolicy.PendingStatus;

    public decimal PartialCostOfGoods { get; init; }
    public bool IsCogsComplete { get; init; } = true;
    public int UncostedTransactionCount { get; init; }
    public decimal UncostedSalesAmount { get; init; }
    public decimal StructuredOperatingExpenses { get; init; }
    public decimal OperatingExpenseGst { get; init; }
    public IReadOnlyDictionary<string, decimal> OperatingExpensesByCategory { get; init; } =
        new Dictionary<string, decimal>();
    public decimal? DirectProfit { get; init; }
    public decimal? DirectMarginPercent { get; init; }
    public NayaxProcessingFeeResult NayaxProcessingFees { get; init; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null);

    /// <summary>
    /// How well recorded provider coverage accounts for this period's provider-sourced figures
    /// (issue #730, parent #329 decision P9). <see cref="ReportProviderAvailabilityDto.Sales"/>
    /// governs <see cref="Sales"/>, <see cref="CardSales"/>, <see cref="CashSales"/>, their
    /// transaction counts, <see cref="CostOfGoods"/>/<see cref="PartialCostOfGoods"/>,
    /// <see cref="GstOnSales"/>, <see cref="TaxableSales"/>, <see cref="GstFreeSales"/>,
    /// <see cref="NoGstRuleSales"/>, <see cref="SiteCommission"/> and <see cref="GrossProfit"/>;
    /// <see cref="ReportProviderAvailabilityDto.Fees"/> governs
    /// <see cref="Fees"/>, <see cref="NayaxFeesExGst"/>, <see cref="NayaxFeesIncludingGst"/>,
    /// <see cref="GstOnFees"/> and <see cref="ActualReimbursement"/>; and
    /// <see cref="ReportProviderAvailabilityDto.Combined"/> governs the
    /// figures that depend on both - <see cref="NetSettlement"/>, <see cref="NetProfit"/>,
    /// <see cref="NetMarginPercent"/>, <see cref="DirectProfit"/>,
    /// <see cref="DirectMarginPercent"/>, <see cref="ExpectedReimbursement"/> and
    /// <see cref="ReimbursementDifference"/>. The locally recorded figures - delivery and package costs, operating
    /// expenses and their GST - come from this business's own records and carry no provider state:
    /// they stay visible and exact whatever the provider data looks like.
    ///
    /// A state other than <c>Complete</c> means the amounts shown are not known to be all of them,
    /// so a presentation must show the state rather than the number's zero.
    /// </summary>
    public ReportProviderAvailabilityDto ProviderAvailability { get; init; } =
        ReportProviderAvailabilityDto.Undecided;
}
