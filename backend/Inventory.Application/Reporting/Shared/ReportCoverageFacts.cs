using Inventory.Domain.Reporting.Availability;

namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// The coverage evidence one report period rests on, as a facts provider reports it (issue #730 for
/// the GST and bookkeeping reports, issue #731 for every other sales-based report and export).
///
/// <para>It exists so the six remaining reports do not each carry four loose fields and repeat the
/// same two <see cref="ReportAvailabilityPolicy.Decide"/> calls. The period instants travel with the
/// coverage deliberately: the adapter that queried the rows is the only thing that knows which
/// window it filtered them by - the daily report selects sales between business-day midnights, the
/// others between plain calendar-date bounds - and a coverage range may only be compared with the
/// window the figures were actually measured over.</para>
/// </summary>
public sealed record ReportCoverageFacts
{
    /// <summary>
    /// The period's inclusive start instant, in the same UTC space the report filtered its rows by.
    /// <see langword="null"/> on a coverage record nothing decided, which reports
    /// <see cref="ReportProviderAvailabilityDto.Undecided"/> rather than a state.
    /// </summary>
    public DateTime? PeriodStartUtc { get; init; }

    /// <summary>The period's exclusive end instant, in that same UTC space.</summary>
    public DateTime? PeriodEndExclusiveUtc { get; init; }

    /// <summary>
    /// Every recorded coverage range overlapping the period that accounts for sale instants - a
    /// fully successful sales synchronization or a committed sales file import. Unordered and
    /// expected to overlap; the Domain policy clips and merges them.
    /// </summary>
    public IReadOnlyList<ReportCoverageRange> SalesCoverage { get; init; } = [];

    /// <summary>The same, for the reimbursement periods committed settlement imports accounted for.</summary>
    public IReadOnlyList<ReportCoverageRange> SettlementCoverage { get; init; } = [];

    /// <summary>
    /// Whether the period and scope holds any stored provider sale at all, counted before the
    /// completed-sale filter. It separates "nothing is recorded and nothing covered this period"
    /// from "rows exist that no coverage accounts for", and is never derived from an amount: a
    /// period holding only cancelled rows has sales data and a gross total of zero.
    /// </summary>
    public bool ContainsSalesRows { get; init; }

    /// <summary>The same, for imported settlement rows overlapping the period.</summary>
    public bool ContainsSettlementRows { get; init; }

    /// <summary>
    /// The two figure families' states and the combined one derived from them, decided once by the
    /// Domain policy. The families are decided separately, from the sources that actually feed them:
    /// sale instants for the sales figures, imported reimbursement periods for the fee and
    /// settlement figures. A settlement file arriving never makes the sales figures complete, and
    /// vice versa.
    ///
    /// <para>A record carrying no period reports <see cref="ReportProviderAvailabilityDto.Undecided"/>
    /// - deliberately <c>Unverified</c> and never <c>Complete</c>, because a state nothing decided
    /// must not license presenting a zero as a real zero.</para>
    /// </summary>
    public ReportProviderAvailabilityDto Availability() =>
        HasPeriod
            ? ReportProviderAvailabilityDto.From(Decide(SalesCoverage, ContainsSalesRows),
                Decide(SettlementCoverage, ContainsSettlementRows))
            : ReportProviderAvailabilityDto.Undecided;

    /// <summary>
    /// The sales family's state alone, for a report that shows no settlement-sourced figure at all -
    /// product profitability and transaction sales. Reporting a settlement state beside figures no
    /// settlement import feeds would label a figure with evidence that does not reach it, which is
    /// exactly what issue #730's split families exist to prevent.
    /// </summary>
    public ReportAvailabilityDto SalesAvailability() =>
        HasPeriod
            ? ReportAvailabilityDto.From(Decide(SalesCoverage, ContainsSalesRows))
            : ReportAvailabilityDto.Undecided;

    private bool HasPeriod =>
        PeriodStartUtc is DateTime start && PeriodEndExclusiveUtc is DateTime end && end > start;

    private ReportAvailability Decide(IReadOnlyList<ReportCoverageRange> coverage, bool hasData) =>
        ReportAvailabilityPolicy.Decide(
            PeriodStartUtc!.Value, PeriodEndExclusiveUtc!.Value, coverage, hasData);
}
