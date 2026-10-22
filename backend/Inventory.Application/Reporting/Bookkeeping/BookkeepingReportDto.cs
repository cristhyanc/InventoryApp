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
    /// The reimbursement this period's card sales and Nayax fees say to expect - card sales less
    /// fees including GST, calculated by <c>DashboardReimbursementPolicy</c> (issue #805). It is a
    /// calculation, never an imported amount, and it is deliberately not
    /// <see cref="NetSettlement"/>, which prefers the imported figure for the period when there is
    /// one. Governed by <see cref="ReportProviderAvailabilityDto.Combined"/>, because it is built
    /// from a sales figure and a fee figure.
    /// </summary>
    public decimal ExpectedReimbursement { get; init; }

    /// <summary>
    /// The net reimbursement the settlement import recorded for this period, matched on its covered
    /// reimbursement period. It is <c>0</c> when no imported row covers the period at all, which
    /// <see cref="ReconciliationStatus"/> reports as <c>Pending</c> and
    /// <see cref="ReportProviderAvailabilityDto.Fees"/> labels - a settlement import that really
    /// settled nothing stays a measured zero in a <c>Complete</c> period, and the two must never be
    /// read as each other.
    /// </summary>
    public decimal ActualReimbursement { get; init; }

    /// <summary>
    /// <see cref="ActualReimbursement"/> less <see cref="ExpectedReimbursement"/>, signed: negative
    /// means the import paid less than this period's card sales and fees expected. Governed by
    /// <see cref="ReportProviderAvailabilityDto.Combined"/>, because a difference of zero between
    /// two sides that were never both measured is the most misleading figure in this comparison.
    /// </summary>
    public decimal ReimbursementDifference { get; init; }

    /// <summary>
    /// Whether the comparison reconciled: an imported row exists for the period and the absolute
    /// difference is within <see cref="ReconciliationTolerance"/>. A <c>Pending</c> period is never
    /// reconciled.
    /// </summary>
    public bool IsReconciled { get; init; }

    /// <summary>
    /// <c>Pending</c> (no imported row covers the period), <c>Reconciled</c> or <c>Needs Review</c>,
    /// from the same policy and the same vocabulary the reporting dashboard published.
    /// </summary>
    public string ReconciliationStatus { get; init; } = PendingReconciliationStatus;

    /// <summary>
    /// The tolerance the status was decided with, reported so no presentation or export restates the
    /// repository's `$0.01` rule itself.
    /// </summary>
    public decimal ReconciliationTolerance { get; init; } = DashboardReimbursementPolicy.Tolerance;

    /// <summary>
    /// The status a report with no imported settlement row carries, named so a presentation can
    /// recognise it without repeating the literal (the dashboard's own card does the same).
    /// </summary>
    public const string PendingReconciliationStatus = "Pending";

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
    /// <see cref="GstOnSales"/>, <see cref="SiteCommission"/> and <see cref="GrossProfit"/>;
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
