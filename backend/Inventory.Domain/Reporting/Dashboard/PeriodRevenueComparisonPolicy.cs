namespace Inventory.Domain.Reporting.Dashboard;

/// <summary>
/// The week-on-week revenue comparison for the home Dashboard's sales card (issue #459).
/// <see cref="ChangeAmount"/>/<see cref="ChangePercent"/>/<see cref="PriorPeriodSales"/> are
/// <c>null</c> whenever the comparison is unavailable, and <see cref="ChangePercent"/> is also
/// <c>null</c> when the prior period's revenue is zero: there is no honest percentage change from
/// nothing, and presenting one as an infinite or 100% rise would misstate a financial figure.
/// <see cref="Note"/> carries the reason the UI must show instead.
/// </summary>
public readonly record struct PeriodRevenueComparison(
    bool IsAvailable,
    decimal? PriorPeriodSales,
    decimal? ChangeAmount,
    decimal? ChangePercent,
    string? Note);

/// <summary>
/// The deterministic current-versus-prior-period revenue comparison rule. The percentage formula is
/// the one the Dashboard already used - <c>(current - prior) / prior * 100</c>, undefined when prior
/// is zero - lifted into the backend so the figure a card shows is calculated once, by the layer
/// that owns financial definitions, rather than per presentation.
///
/// Availability is a data-coverage decision, not a formatting one. A prior period that the recorded
/// completed sales do not reach back to would compare this week's revenue against a period for which
/// no sales were ever recorded, which reads as a collapse in trade rather than as missing data, so
/// it is reported unavailable instead of as a real zero.
/// </summary>
public static class PeriodRevenueComparisonPolicy
{
    /// <summary>The note when recorded sales do not reach back to the start of the prior period.</summary>
    public const string PriorPeriodNotCoveredNote =
        "Recorded completed sales do not cover the comparable period last week, so a week-on-week comparison is unavailable.";

    /// <summary>The note when the prior period is covered but contains no completed sales.</summary>
    public const string ZeroPriorPeriodNote =
        "There were no completed sales in the comparable period last week, so a percentage change is undefined.";

    /// <summary>
    /// Compares a period's revenue against the prior comparable period's.
    /// <paramref name="earliestRecordedSaleUtc"/> is the instant of the business's earliest recorded
    /// completed sale, or <c>null</c> when it has none; the comparison is available only when that
    /// instant is at or before <paramref name="priorPeriodStartUtc"/>, so the prior period is fully
    /// covered by recorded data.
    /// </summary>
    public static PeriodRevenueComparison Compare(
        decimal currentPeriodSales,
        decimal priorPeriodSales,
        DateTime priorPeriodStartUtc,
        DateTime? earliestRecordedSaleUtc)
    {
        if (earliestRecordedSaleUtc is not DateTime earliest || earliest > priorPeriodStartUtc)
            return new PeriodRevenueComparison(false, null, null, null, PriorPeriodNotCoveredNote);

        var changeAmount = currentPeriodSales - priorPeriodSales;

        return priorPeriodSales == 0m
            ? new PeriodRevenueComparison(true, priorPeriodSales, changeAmount, null, ZeroPriorPeriodNote)
            : new PeriodRevenueComparison(
                true, priorPeriodSales, changeAmount, changeAmount / priorPeriodSales * 100m, null);
    }
}
