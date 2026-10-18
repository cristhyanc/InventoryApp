using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.ProductProfitability;

public record ProductProfitabilityReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<ProductProfitabilityRowDto> Rows,
    ReportingDataQualityDto DataQuality)
{
    /// <summary>
    /// How well recorded provider coverage accounts for this period's stored sales (issue #731,
    /// under issue #730's model). It governs every figure in the report - each product's sales,
    /// quantity, cost of goods, gross profit and margin - and the absence of rows itself: an empty
    /// product list is only "nothing sold" when coverage says the period was read end to end.
    ///
    /// <para>One state, not the two-family split the bookkeeping and daily reports carry: this
    /// report shows no settlement-sourced figure, so there is nothing for a settlement state to
    /// label. The Last/Lowest purchase costs beside each product are this business's own purchase
    /// records and carry no provider state at all.</para>
    /// </summary>
    public ReportAvailabilityDto SalesAvailability { get; init; } = ReportAvailabilityDto.Undecided;
}
