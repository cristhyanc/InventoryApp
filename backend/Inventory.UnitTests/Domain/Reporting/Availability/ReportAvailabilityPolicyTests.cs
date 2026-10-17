using Inventory.Domain.Reporting.Availability;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Availability;

/// <summary>
/// Issue #730 (parent #329 decision P9): the four distinct availability states and the rule that
/// decides between them. These are the cases the issue's acceptance criteria name, expressed against
/// the Domain policy alone so the distinction is pinned independently of either report that shows it.
/// </summary>
public class ReportAvailabilityPolicyTests
{
    private static readonly DateTime PeriodStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEndExclusive = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ReportAvailability Decide(bool hasData, params ReportCoverageRange[] coverage) =>
        ReportAvailabilityPolicy.Decide(PeriodStart, PeriodEndExclusive, coverage, hasData);

    private static ReportCoverageRange Range(DateTime from, DateTime to) => new(from, to);

    [Fact]
    public void No_coverage_and_no_data_is_missing()
    {
        var availability = Decide(hasData: false);

        Assert.Equal(ReportAvailabilityState.Missing, availability.State);
        Assert.Null(availability.AsOfUtc);
    }

    [Fact]
    public void Data_with_no_coverage_at_all_is_unverified_rather_than_missing_or_complete()
    {
        // Sales stored before coverage recording began: the rows are real, and nothing recorded
        // claims they are all of them.
        var availability = Decide(hasData: true);

        Assert.Equal(ReportAvailabilityState.Unverified, availability.State);
    }

    [Fact]
    public void Coverage_spanning_the_whole_period_is_complete_even_with_no_data()
    {
        // The only state in which a zero is a real zero.
        var availability = Decide(hasData: false, Range(PeriodStart.AddDays(-10), PeriodEndExclusive.AddDays(3)));

        Assert.Equal(ReportAvailabilityState.Complete, availability.State);
        Assert.Null(availability.AsOfUtc);
    }

    [Fact]
    public void Coverage_from_the_period_start_that_stops_early_is_stale_as_of_its_end()
    {
        // The shape a disconnected or simply unsynchronized provider leaves: previously imported
        // sales stay reported, labelled up to the instant coverage reaches.
        var coverageEnd = new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc);

        var availability = Decide(hasData: true, Range(PeriodStart.AddDays(-5), coverageEnd));

        Assert.Equal(ReportAvailabilityState.Stale, availability.State);
        Assert.Equal(coverageEnd, availability.AsOfUtc);
    }

    [Fact]
    public void A_gap_in_the_middle_of_the_period_is_unverified_not_stale()
    {
        // A failed run between two successful ones: the period is not accounted for end to end, and
        // the part after the gap is not "as of" anything.
        var availability = Decide(
            hasData: true,
            Range(PeriodStart, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)),
            Range(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), PeriodEndExclusive));

        Assert.Equal(ReportAvailabilityState.Unverified, availability.State);
        Assert.Null(availability.AsOfUtc);
    }

    [Fact]
    public void Coverage_that_starts_after_the_period_start_is_unverified()
    {
        var availability = Decide(
            hasData: true, Range(PeriodStart.AddDays(3), PeriodEndExclusive.AddDays(1)));

        Assert.Equal(ReportAvailabilityState.Unverified, availability.State);
    }

    [Fact]
    public void Successive_overlapping_runs_merge_into_one_complete_period()
    {
        // Each successful sync records its own window; consecutive windows overlap by design, and a
        // period they jointly span must not be reported as having a gap between them.
        var availability = Decide(
            hasData: true,
            Range(PeriodStart.AddDays(-1), new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc)),
            Range(new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc), PeriodEndExclusive));

        Assert.Equal(ReportAvailabilityState.Complete, availability.State);
    }

    [Fact]
    public void Two_runs_that_meet_exactly_leave_no_gap()
    {
        var meeting = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

        var availability = Decide(
            hasData: true, Range(PeriodStart, meeting), Range(meeting, PeriodEndExclusive));

        Assert.Equal(ReportAvailabilityState.Complete, availability.State);
    }

    [Fact]
    public void An_empty_or_inverted_recorded_range_covers_nothing()
    {
        // A run that read nothing it could claim a range for must not look like coverage.
        var availability = Decide(
            hasData: true, Range(PeriodStart, PeriodStart), Range(PeriodEndExclusive, PeriodStart));

        Assert.Equal(ReportAvailabilityState.Unverified, availability.State);
    }

    [Fact]
    public void Coverage_entirely_outside_the_period_is_not_coverage_of_it()
    {
        var availability = Decide(
            hasData: false, Range(PeriodEndExclusive, PeriodEndExclusive.AddDays(5)));

        Assert.Equal(ReportAvailabilityState.Missing, availability.State);
    }

    [Theory]
    [InlineData(ReportAvailabilityState.Complete, ReportAvailabilityState.Stale, ReportAvailabilityState.Stale)]
    [InlineData(ReportAvailabilityState.Stale, ReportAvailabilityState.Unverified, ReportAvailabilityState.Unverified)]
    [InlineData(ReportAvailabilityState.Unverified, ReportAvailabilityState.Missing, ReportAvailabilityState.Missing)]
    [InlineData(ReportAvailabilityState.Complete, ReportAvailabilityState.Complete, ReportAvailabilityState.Complete)]
    public void A_combined_figure_takes_the_least_complete_of_its_sources(
        ReportAvailabilityState first, ReportAvailabilityState second, ReportAvailabilityState expected)
    {
        var combined = ReportAvailabilityPolicy.LeastComplete(
            new ReportAvailability(first, null), new ReportAvailability(second, null));

        Assert.Equal(expected, combined.State);
    }

    [Fact]
    public void The_least_complete_source_keeps_its_own_as_of_instant()
    {
        var asOf = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc);

        var combined = ReportAvailabilityPolicy.LeastComplete(
            new ReportAvailability(ReportAvailabilityState.Complete, null),
            new ReportAvailability(ReportAvailabilityState.Stale, asOf));

        Assert.Equal(asOf, combined.AsOfUtc);
    }
}
