using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Domain.PriceCatalogue;

/// <summary>
/// Issue #1108's due rule, per <strong>product and supplier pair</strong>: whether that pair is owed
/// a search on one date of the collection calendar.
///
/// <para>Every case here states a schedule, a date, an effective-from instant, the pair's latest
/// answer and whether it is in flight, and asserts the one answer. The occurrence start is derived
/// from the schedule and the date through <see cref="SupplierSearchSchedulePolicy"/> and an explicit
/// offset, so a case reads as "this supplier, on this day, with this history" rather than as a pile
/// of instants - and the daylight-saving cases below are the two days a year that offset is not the
/// one beside them.</para>
///
/// <para>The three work-item states - pending in an open run, answered, and finalised as
/// <c>NotSearched</c> - are parameters here and are exercised against the real tables, runs and
/// expiries in <c>PriceCollectorDueWorkListTests</c>.</para>
/// </summary>
public class SupplierSearchDuePolicyTests
{
    /// <summary>
    /// Sydney's standard offset, which applies from the first Sunday in April to the first Sunday in
    /// October. Written out because this suite is about the <em>rule</em>: the conversion itself is
    /// the collection calendar's and is asserted against the platform time-zone database in
    /// <c>SupplierSearchScheduleCalendarTests</c>.
    /// </summary>
    private static readonly TimeSpan StandardTime = TimeSpan.FromHours(10);

    /// <summary>Sydney's daylight-saving offset, which applies for the rest of the year.</summary>
    private static readonly TimeSpan DaylightSavingTime = TimeSpan.FromHours(11);

    /// <summary>
    /// An effective-from instant far enough in the past that it never decides a case: the schedules
    /// below were saved long before any of these dates, so the boundary is the occurrence's.
    /// </summary>
    private static readonly DateTime LongSinceEffective =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    #region Daily

    /// <summary>
    /// A daily supplier's occurrence is today, so an answer from yesterday does not cover today and
    /// an answer from this morning does.
    /// </summary>
    [Fact]
    public void A_daily_pair_is_due_once_a_day()
    {
        var date = new DateOnly(2026, 11, 5);
        var boundary = StartOfDay(date, DaylightSavingTime);

        Assert.True(IsDue(SupplierSearchSchedulePolicy.Daily, date, answeredAtUtc: null));
        Assert.True(IsDue(
            SupplierSearchSchedulePolicy.Daily,
            date,
            answeredAtUtc: boundary.AddMinutes(-1)));
        Assert.False(IsDue(SupplierSearchSchedulePolicy.Daily, date, answeredAtUtc: boundary));
        Assert.False(IsDue(
            SupplierSearchSchedulePolicy.Daily,
            date,
            answeredAtUtc: boundary.AddHours(9)));
    }

    #endregion

    #region Weekly

    /// <summary>
    /// A Thursday-only supplier answered on its Thursday stays covered every day until the next
    /// Thursday, and becomes due again on it. That is the whole of "not searched again until the next
    /// occurrence".
    /// </summary>
    [Fact]
    public void A_weekly_pair_answered_on_its_day_stays_covered_until_the_next_occurrence()
    {
        var schedule = Weekly(DayOfWeek.Thursday);
        var thursday = new DateOnly(2026, 11, 5);
        var answered = StartOfDay(thursday, DaylightSavingTime).AddHours(3);

        for (var day = 0; day <= 6; day++)
        {
            Assert.False(IsDue(schedule, thursday.AddDays(day), answered));
        }

        Assert.True(IsDue(schedule, thursday.AddDays(7), answered));
    }

    /// <summary>
    /// And the catch-up direction: a Thursday nothing answered leaves the pair due on the Friday, the
    /// Saturday and every day until something does.
    /// </summary>
    [Fact]
    public void A_missed_occurrence_leaves_the_pair_due_the_next_day_and_after()
    {
        var schedule = Weekly(DayOfWeek.Thursday);
        var thursday = new DateOnly(2026, 11, 5);

        // The last answer was the previous Thursday, which the current occurrence has moved past.
        var answered = StartOfDay(thursday.AddDays(-7), DaylightSavingTime).AddHours(3);

        for (var day = 0; day <= 6; day++)
        {
            Assert.True(IsDue(schedule, thursday.AddDays(day), answered));
        }
    }

    /// <summary>
    /// A Wednesday-and-Saturday supplier has two occurrences a week, so Saturday's run does not keep
    /// Wednesday covered and Wednesday's does not keep Saturday covered.
    /// </summary>
    [Fact]
    public void A_pair_on_wednesday_and_saturday_is_due_again_at_each_occurrence()
    {
        var schedule = Weekly(DayOfWeek.Wednesday, DayOfWeek.Saturday);
        var wednesday = new DateOnly(2026, 11, 4);
        var saturday = new DateOnly(2026, 11, 7);

        var answeredWednesday = StartOfDay(wednesday, DaylightSavingTime).AddHours(4);

        // Thursday and Friday are still Wednesday's occurrence.
        Assert.False(IsDue(schedule, wednesday.AddDays(1), answeredWednesday));
        Assert.False(IsDue(schedule, wednesday.AddDays(2), answeredWednesday));

        // Saturday is a new occurrence, so Wednesday's answer no longer covers it.
        Assert.True(IsDue(schedule, saturday, answeredWednesday));

        var answeredSaturday = StartOfDay(saturday, DaylightSavingTime).AddHours(4);
        Assert.False(IsDue(schedule, saturday.AddDays(2), answeredSaturday));

        // And the following Wednesday is the next occurrence again.
        Assert.True(IsDue(schedule, wednesday.AddDays(7), answeredSaturday));
    }

    #endregion

    #region Fortnightly

    [Fact]
    public void A_fortnightly_pair_answered_in_its_on_week_stays_covered_through_the_off_week()
    {
        var schedule = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday);
        var onThursday = new DateOnly(2026, 11, 12);
        var answered = StartOfDay(onThursday, DaylightSavingTime).AddHours(2);

        // Every day of the on week's remainder and of the whole off week.
        for (var day = 0; day <= 13; day++)
        {
            Assert.False(IsDue(schedule, onThursday.AddDays(day), answered));
        }

        // The next on week's Thursday is a new occurrence.
        Assert.True(IsDue(schedule, onThursday.AddDays(14), answered));
    }

    [Fact]
    public void A_fortnightly_pair_follows_the_same_alternation_across_a_year_end()
    {
        var schedule = Fortnightly(new DateOnly(2026, 12, 28), DayOfWeek.Thursday);
        var answered = StartOfDay(new DateOnly(2026, 12, 31), DaylightSavingTime).AddHours(2);

        // The off week in January is still covered by 31 December's answer.
        Assert.False(IsDue(schedule, new DateOnly(2027, 1, 7), answered));
        Assert.False(IsDue(schedule, new DateOnly(2027, 1, 13), answered));

        // 14 January is the next on week's Thursday.
        Assert.True(IsDue(schedule, new DateOnly(2027, 1, 14), answered));
    }

    /// <summary>
    /// A date <em>before</em> the start date follows the same alternation rather than being held
    /// back by it: the start date says which weeks are on, not when collection begins.
    /// </summary>
    [Fact]
    public void A_fortnightly_pair_before_the_start_date_follows_the_same_alternation()
    {
        var schedule = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday);

        // 29 October 2026 is a Thursday two weeks before the anchor week, so it is an on week.
        var earlier = new DateOnly(2026, 10, 29);
        Assert.True(IsDue(schedule, earlier, answeredAtUtc: null));

        var answered = StartOfDay(earlier, DaylightSavingTime).AddHours(2);
        Assert.False(IsDue(schedule, earlier, answered));

        // And the Thursday in between is an off week, still covered by that answer.
        Assert.False(IsDue(schedule, new DateOnly(2026, 11, 5), answered));
    }

    /// <summary>
    /// A mid-week start date is read as its own Monday, so it selects the same occurrences and leaves
    /// the same pairs due.
    /// </summary>
    [Fact]
    public void A_mid_week_start_date_leaves_exactly_the_same_pairs_due()
    {
        var fromMonday = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday);
        var fromFriday = Fortnightly(new DateOnly(2026, 11, 13), DayOfWeek.Thursday);
        var answered = StartOfDay(new DateOnly(2026, 11, 12), DaylightSavingTime).AddHours(2);

        for (var day = 0; day < 28; day++)
        {
            var date = new DateOnly(2026, 11, 1).AddDays(day);

            Assert.Equal(IsDue(fromMonday, date, answered), IsDue(fromFriday, date, answered));
        }
    }

    #endregion

    #region The effective-from boundary

    /// <summary>
    /// A pair answered on Monday morning, whose schedule is then edited on Monday afternoon, is due
    /// again on that same Monday: the boundary moved past the answer.
    /// </summary>
    [Fact]
    public void A_schedule_edited_after_an_answer_makes_the_pair_due_again_the_same_day()
    {
        var monday = new DateOnly(2026, 11, 9);
        var morning = StartOfDay(monday, DaylightSavingTime).AddHours(7);
        var afternoon = StartOfDay(monday, DaylightSavingTime).AddHours(15);

        // Before the edit the morning's answer covers the day.
        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(monday, DaylightSavingTime),
            LongSinceEffective,
            morning,
            hasPendingWorkItem: false));

        // The edit's instant becomes the boundary, and it is after the answer.
        Assert.True(SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(monday, DaylightSavingTime),
            afternoon,
            morning,
            hasPendingWorkItem: false));
    }

    /// <summary>
    /// The same mechanism is what makes switching <em>Search prices</em> back on re-issue the work:
    /// the boundary advances to that save's instant, so an answer from earlier the same day no longer
    /// covers the pair.
    /// </summary>
    [Fact]
    public void Switching_searching_back_on_makes_the_pair_due_again_the_same_day()
    {
        var monday = new DateOnly(2026, 11, 9);
        var answered = StartOfDay(monday, DaylightSavingTime).AddHours(2);
        var switchedOn = StartOfDay(monday, DaylightSavingTime).AddHours(3);

        Assert.True(SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(monday, DaylightSavingTime),
            switchedOn,
            answered,
            hasPendingWorkItem: false));
    }

    /// <summary>
    /// An unrelated edit leaves the boundary where it was, so an answer that covered the pair still
    /// covers it. (Which edits move the stored instant is the save's own decision, asserted in
    /// <c>EfSupplierPresetStoreTests</c>; this is what "the boundary did not move" means for the
    /// rule.)
    /// </summary>
    [Fact]
    public void A_boundary_that_did_not_move_leaves_an_existing_answer_covering_the_pair()
    {
        var monday = new DateOnly(2026, 11, 9);
        var answered = StartOfDay(monday, DaylightSavingTime).AddHours(2);

        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(monday, DaylightSavingTime),
            LongSinceEffective,
            answered,
            hasPendingWorkItem: false));
    }

    /// <summary>
    /// The boundary is the later of the two instants, whichever that is - which is the whole of
    /// "an answer from Monday morning does not cover a schedule changed on Monday afternoon".
    /// </summary>
    [Fact]
    public void The_coverage_boundary_is_the_later_of_the_occurrence_and_the_effective_from()
    {
        var occurrence = new DateTime(2026, 11, 9, 13, 0, 0, DateTimeKind.Utc);
        var earlier = occurrence.AddDays(-30);
        var later = occurrence.AddHours(4);

        Assert.Equal(occurrence, SupplierSearchDuePolicy.CoverageBoundary(occurrence, earlier));
        Assert.Equal(later, SupplierSearchDuePolicy.CoverageBoundary(occurrence, later));
        Assert.Equal(occurrence, SupplierSearchDuePolicy.CoverageBoundary(occurrence, occurrence));
    }

    #endregion

    #region The work-item states

    [Fact]
    public void A_pair_in_flight_is_not_due_however_stale_it_is()
    {
        var date = new DateOnly(2026, 11, 9);

        // Never answered at all, which is otherwise the most due a pair can be.
        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(date, DaylightSavingTime),
            LongSinceEffective,
            latestAnsweredAtUtc: null,
            hasPendingWorkItem: true));

        // And an answer from before the boundary does not change that either.
        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(date, DaylightSavingTime),
            LongSinceEffective,
            latestAnsweredAtUtc: StartOfDay(date.AddDays(-9), DaylightSavingTime),
            hasPendingWorkItem: true));
    }

    /// <summary>
    /// What counts as an answer: everything a collector or a closure can record except
    /// <c>NotSearched</c>, which says nobody looked. An empty outcome is not an answer either - that
    /// work item is pending, which is the blocking state rather than the covering one.
    /// </summary>
    [Theory]
    [InlineData(PriceCollectorWorkItemOutcome.Complete, true)]
    [InlineData(PriceCollectorWorkItemOutcome.Truncated, true)]
    [InlineData(PriceCollectorWorkItemOutcome.NoMatches, true)]
    [InlineData(PriceCollectorWorkItemOutcome.Failed, true)]
    [InlineData(PriceCollectorWorkItemOutcome.NotSearched, false)]
    public void An_answer_is_every_recorded_outcome_except_not_searched(
        PriceCollectorWorkItemOutcome outcome,
        bool expected)
    {
        Assert.Equal(expected, SupplierSearchDuePolicy.IsAnswered(outcome));
    }

    [Fact]
    public void An_empty_or_undeclared_outcome_is_not_an_answer()
    {
        Assert.False(SupplierSearchDuePolicy.IsAnswered(null));
        Assert.False(SupplierSearchDuePolicy.IsAnswered((PriceCollectorWorkItemOutcome)99));
    }

    /// <summary>
    /// A newly tracked product has no answered work item at all, so it is due at once whatever its
    /// supplier's schedule says.
    /// </summary>
    [Fact]
    public void A_pair_that_has_never_been_answered_is_due_at_once()
    {
        foreach (var schedule in new[]
        {
            SupplierSearchSchedulePolicy.Daily,
            Weekly(DayOfWeek.Thursday),
            Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday),
        })
        {
            Assert.True(IsDue(schedule, new DateOnly(2026, 11, 20), answeredAtUtc: null));
        }
    }

    #endregion

    #region Daylight saving

    /// <summary>
    /// The day Sydney <strong>starts</strong> daylight saving, 4 October 2026. Midnight is still
    /// standard time, so the day starts at 14:00 UTC the day before - an hour later than it would on
    /// the day after. An answer recorded at 13:30 UTC on 3 October is 23:30 Sydney on the 3rd, which
    /// is <em>before</em> the 4th's occurrence, so the pair is still due.
    /// </summary>
    [Fact]
    public void The_day_daylight_saving_starts_begins_at_standard_time_midnight()
    {
        var date = new DateOnly(2026, 10, 4);
        var startOfDay = StartOfDay(date, StandardTime);

        Assert.Equal(new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc), startOfDay);

        Assert.True(SupplierSearchDuePolicy.IsPairDue(
            startOfDay,
            LongSinceEffective,
            latestAnsweredAtUtc: new DateTime(2026, 10, 3, 13, 30, 0, DateTimeKind.Utc),
            hasPendingWorkItem: false));

        // And an answer after that instant - 00:30 Sydney on the 4th - does cover it.
        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            startOfDay,
            LongSinceEffective,
            latestAnsweredAtUtc: new DateTime(2026, 10, 3, 14, 30, 0, DateTimeKind.Utc),
            hasPendingWorkItem: false));
    }

    /// <summary>
    /// The day Sydney <strong>ends</strong> daylight saving, 5 April 2026. Midnight is still
    /// daylight-saving time, so the day starts at 13:00 UTC the day before. The hour between 13:00
    /// and 14:00 UTC belongs to the 5th here and to the 4th on the October day above, which is
    /// exactly why the conversion is not a constant.
    /// </summary>
    [Fact]
    public void The_day_daylight_saving_ends_begins_at_daylight_saving_midnight()
    {
        var date = new DateOnly(2026, 4, 5);
        var startOfDay = StartOfDay(date, DaylightSavingTime);

        Assert.Equal(new DateTime(2026, 4, 4, 13, 0, 0, DateTimeKind.Utc), startOfDay);

        Assert.True(SupplierSearchDuePolicy.IsPairDue(
            startOfDay,
            LongSinceEffective,
            latestAnsweredAtUtc: new DateTime(2026, 4, 4, 12, 30, 0, DateTimeKind.Utc),
            hasPendingWorkItem: false));

        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            startOfDay,
            LongSinceEffective,
            latestAnsweredAtUtc: new DateTime(2026, 4, 4, 13, 30, 0, DateTimeKind.Utc),
            hasPendingWorkItem: false));
    }

    /// <summary>
    /// A weekly supplier whose occurrence falls on one side of a transition and whose "today" falls
    /// on the other: the occurrence's own offset is what decides the boundary, not today's. The
    /// Thursday before the April 2026 transition is the 2nd, and Monday the 6th is after it.
    /// </summary>
    [Fact]
    public void An_occurrence_before_a_transition_keeps_its_own_offset()
    {
        var schedule = Weekly(DayOfWeek.Thursday);

        Assert.Equal(
            new DateOnly(2026, 4, 2),
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 4, 6)));

        // 2 April 2026 is still daylight-saving time, so its midnight is 1 April 13:00 UTC.
        var occurrence = StartOfDay(new DateOnly(2026, 4, 2), DaylightSavingTime);
        Assert.Equal(new DateTime(2026, 4, 1, 13, 0, 0, DateTimeKind.Utc), occurrence);

        Assert.False(SupplierSearchDuePolicy.IsPairDue(
            occurrence,
            LongSinceEffective,
            latestAnsweredAtUtc: occurrence.AddHours(6),
            hasPendingWorkItem: false));
        Assert.True(SupplierSearchDuePolicy.IsPairDue(
            occurrence,
            LongSinceEffective,
            latestAnsweredAtUtc: occurrence.AddMinutes(-1),
            hasPendingWorkItem: false));
    }

    #endregion

    /// <summary>
    /// The rule as a reader states it: this schedule, on this date of the collection calendar, with
    /// this latest answer and nothing in flight.
    /// </summary>
    private static bool IsDue(
        SupplierSearchSchedule schedule,
        DateOnly date,
        DateTime? answeredAtUtc) =>
        SupplierSearchDuePolicy.IsPairDue(
            StartOfDay(
                SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, date),
                OffsetOn(SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, date))),
            LongSinceEffective,
            answeredAtUtc,
            hasPendingWorkItem: false);

    private static DateTime StartOfDay(DateOnly date, TimeSpan offsetFromUtc) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) - offsetFromUtc, DateTimeKind.Utc);

    /// <summary>
    /// Sydney's offset on one date, for the cases that are not about a transition: daylight saving
    /// from the first Sunday in October to the first Sunday in April, and standard time in between.
    /// The dates used here are all comfortably inside one period or the other.
    /// </summary>
    private static TimeSpan OffsetOn(DateOnly date) =>
        date >= new DateOnly(date.Year, 4, 6) && date < new DateOnly(date.Year, 10, 4)
            ? StandardTime
            : DaylightSavingTime;

    private static SupplierSearchSchedule Weekly(params DayOfWeek[] days) =>
        SupplierSearchSchedulePolicy.Require(
            new SupplierSearchSchedule(SupplierSearchFrequency.Weekly, days, null));

    private static SupplierSearchSchedule Fortnightly(DateOnly startDate, params DayOfWeek[] days) =>
        SupplierSearchSchedulePolicy.Require(
            new SupplierSearchSchedule(SupplierSearchFrequency.Fortnightly, days, startDate));
}
