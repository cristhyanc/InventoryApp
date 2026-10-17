using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Daily;

public record DailyReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<DailyReportRowDto> Rows,
    ReportingDataQualityDto DataQuality,
    DailyReportTotalsDto? Totals = null)
{
    /// <summary>
    /// How well recorded provider coverage accounts for this period's provider-sourced figures
    /// (issue #731, under issue #730's model). <c>Sales</c> governs gross sales, the card/cash
    /// split, the transaction counts, cost of goods, gross profit and the margin; <c>Fees</c>
    /// governs the imported reimbursement and net reimbursement; <c>Combined</c> governs the
    /// reconciliation status, which compares the two. Only <c>Complete</c> permits a zero in the
    /// figures it governs to be presented as a real zero.
    ///
    /// <para>It is decided for the report period, which is the window the coverage record makes a
    /// claim about. The per-day rows are the same data broken down and carry no separate state.</para>
    /// </summary>
    public ReportProviderAvailabilityDto ProviderAvailability { get; init; } = ReportProviderAvailabilityDto.Undecided;
}
