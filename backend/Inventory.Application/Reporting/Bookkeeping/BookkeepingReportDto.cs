using Inventory.Application.Reporting.Shared;

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
    /// <see cref="GstOnSales"/>, <see cref="SiteCommission"/>, <see cref="NetSettlement"/> and every
    /// profit and margin built on them; <see cref="ReportProviderAvailabilityDto.Fees"/> governs
    /// <see cref="Fees"/>, <see cref="NayaxFeesExGst"/>, <see cref="NayaxFeesIncludingGst"/> and
    /// <see cref="GstOnFees"/>. The locally recorded figures - delivery and package costs, operating
    /// expenses and their GST - come from this business's own records and carry no provider state:
    /// they stay visible and exact whatever the provider data looks like.
    ///
    /// A state other than <c>Complete</c> means the amounts shown are not known to be all of them,
    /// so a presentation must show the state rather than the number's zero.
    /// </summary>
    public ReportProviderAvailabilityDto ProviderAvailability { get; init; } =
        ReportProviderAvailabilityDto.Undecided;
}
