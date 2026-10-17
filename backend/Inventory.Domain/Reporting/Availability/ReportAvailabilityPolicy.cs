namespace Inventory.Domain.Reporting.Availability;

/// <summary>
/// The one place a report period, the coverage recorded for it and whether it holds any data become
/// one of the four <see cref="ReportAvailabilityState"/> answers (issue #730, parent #329 decision
/// P9). Deterministic and dependency-free: every report family that shows a provider figure decides
/// its state here rather than inventing its own "looks empty" rule, which is how a missing figure
/// stops being indistinguishable from a real zero.
/// </summary>
public static class ReportAvailabilityPolicy
{
    /// <summary>
    /// Decides the availability of a report period from the coverage recorded for it.
    /// </summary>
    /// <param name="periodStartUtc">The period's inclusive start instant.</param>
    /// <param name="periodEndExclusiveUtc">The period's exclusive end instant.</param>
    /// <param name="coverage">
    /// Every recorded coverage range of the relevant provider and sources. Order, overlaps and
    /// ranges reaching outside the period are all expected: successive successful runs overlap by
    /// design, and this method clips and merges them itself.
    /// </param>
    /// <param name="hasData">
    /// Whether the period holds any stored provider data at all, which is what separates
    /// <see cref="ReportAvailabilityState.Missing"/> ("nothing is known and there is nothing") from
    /// <see cref="ReportAvailabilityState.Unverified"/> ("there is something, and coverage does not
    /// account for it").
    /// </param>
    public static ReportAvailability Decide(
        DateTime periodStartUtc,
        DateTime periodEndExclusiveUtc,
        IEnumerable<ReportCoverageRange> coverage,
        bool hasData)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        // A period with no extent cannot be partially covered; it is complete or it is nothing.
        if (periodEndExclusiveUtc <= periodStartUtc)
            return new ReportAvailability(ReportAvailabilityState.Complete, null);

        var merged = Merge(Clip(coverage, periodStartUtc, periodEndExclusiveUtc));

        if (merged.Count == 0)
            return new ReportAvailability(
                hasData ? ReportAvailabilityState.Unverified : ReportAvailabilityState.Missing, null);

        // One clipped range can only start at the period start and end at the period end when the
        // original spanned the whole period, so this is the complete case and nothing else is.
        if (merged.Count == 1 && merged[0].FromUtc <= periodStartUtc && merged[0].ToUtc >= periodEndExclusiveUtc)
            return new ReportAvailability(ReportAvailabilityState.Complete, null);

        // Covered from the period start, then nothing: the shape a provider that stopped being read
        // leaves behind. Everything up to that instant is accounted for; the rest is simply not yet
        // known, which is a different claim from a gap somebody's failed run left in the middle.
        if (merged.Count == 1 && merged[0].FromUtc <= periodStartUtc)
            return new ReportAvailability(ReportAvailabilityState.Stale, merged[0].ToUtc);

        // Anything else is a hole coverage does not close: a late start, a gap between two runs, or
        // both. Partial figures stay visible, but never as a finished period.
        return new ReportAvailability(ReportAvailabilityState.Unverified, null);
    }

    /// <summary>
    /// The availability of a figure derived from several provider sources: the least complete of
    /// them, by the severity order <see cref="ReportAvailabilityState"/> declares. A combined figure
    /// is only as trustworthy as its weakest input, so net GST is never complete while the fees that
    /// reduce it are missing - and a combination never claims a state neither input holds.
    /// </summary>
    public static ReportAvailability LeastComplete(ReportAvailability first, ReportAvailability second) =>
        second.State < first.State ? second : first;

    private static IEnumerable<ReportCoverageRange> Clip(
        IEnumerable<ReportCoverageRange> coverage, DateTime startUtc, DateTime endExclusiveUtc)
    {
        foreach (var range in coverage)
        {
            if (range.IsEmpty)
                continue;

            var clipped = new ReportCoverageRange(
                range.FromUtc < startUtc ? startUtc : range.FromUtc,
                range.ToUtc > endExclusiveUtc ? endExclusiveUtc : range.ToUtc);
            if (!clipped.IsEmpty)
                yield return clipped;
        }
    }

    /// <summary>
    /// Merges overlapping and touching ranges into the fewest that cover the same time. Touching
    /// counts as one range: two consecutive successful runs that meet exactly leave no uncovered
    /// instant between them, so the period must not be reported as having a gap there.
    /// </summary>
    private static List<ReportCoverageRange> Merge(IEnumerable<ReportCoverageRange> clipped)
    {
        var ordered = clipped.OrderBy(range => range.FromUtc).ThenBy(range => range.ToUtc).ToList();
        var merged = new List<ReportCoverageRange>(ordered.Count);

        foreach (var range in ordered)
        {
            if (merged.Count > 0 && range.FromUtc <= merged[^1].ToUtc)
            {
                if (range.ToUtc > merged[^1].ToUtc)
                    merged[^1] = merged[^1] with { ToUtc = range.ToUtc };
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }
}
