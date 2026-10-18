using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Transactions;

public record TransactionSalesReportDto(
    DateTime From, DateTime To, IReadOnlyList<TransactionSalesRowDto> Rows, TransactionSalesTotalsDto Totals,
    ReportingDataQualityDto DataQuality, int Page, int PageSize, int TotalCount,
    TransactionSalesFilterOptionsDto FilterOptions)
{
    /// <summary>
    /// How well recorded provider coverage accounts for this period's stored sales (issue #731,
    /// under issue #730's model). It governs every total this report shows - sales, the card/cash
    /// split, cost of goods, the profit figures and the estimated fees and commission built on them.
    /// Only <c>Complete</c> permits one of those totals to be presented as a real zero.
    ///
    /// <para>One state, not the two-family split the bookkeeping and daily reports carry: this
    /// report shows no imported settlement figure, and its per-transaction fee is an effective-dated
    /// estimate over the stored card transactions, so labelling it with settlement evidence would
    /// attach a state to a figure that evidence does not feed.</para>
    /// </summary>
    public ReportAvailabilityDto SalesAvailability { get; init; } = ReportAvailabilityDto.Undecided;
}
