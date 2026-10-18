using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.MachineProfitability;

public record MachineProfitabilityReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<MachineProfitabilityRowDto> Rows,
    ReportingDataQualityDto DataQuality)
{
    /// <summary>
    /// How well recorded provider coverage accounts for this period's provider-sourced figures
    /// (issue #731, under issue #730's model). <c>Sales</c> governs each machine's sales, the
    /// card/cash split, quantity, cost of goods, gross profit, margin and site commission, and the
    /// machine list itself; <c>Fees</c> governs the actual part of its Nayax processing fees;
    /// <c>Combined</c> governs direct profit and its margin, which subtract both. The per-machine
    /// direct operating expenses are this business's own records and carry no provider state.
    /// </summary>
    public ReportProviderAvailabilityDto ProviderAvailability { get; init; } = ReportProviderAvailabilityDto.Undecided;
}
