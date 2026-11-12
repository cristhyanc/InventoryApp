using System.Globalization;
using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Time;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Time;

/// <summary>
/// The platform collection calendar of issue #1108, against the host's own time-zone database.
///
/// <para>What is under test is the one thing this adapter decides and the Domain cannot: the instant
/// a day of <c>SupplierSearchSchedulePolicy.TimeZoneId</c> starts at. The due rule compares a pair's
/// recorded answer - a true instant - against that, so an offset that was an hour out would move
/// which day an answer belonged to and either re-pay for a search or skip one.</para>
///
/// <para>It is an integration test rather than a unit test for the reason
/// <see cref="ZonedBusinessCalendarTests"/> is one: the answer comes from the platform's zone rules,
/// and a hand-rolled offset table in a test double would be a second answer to what a day starts
/// at.</para>
/// </summary>
public class SupplierSearchScheduleCalendarTests
{
    /// <summary>
    /// The collection calendar is the declared platform zone and nothing else - not the host's, and
    /// not UTC. A host running in UTC, which the API does, would otherwise date every occurrence ten
    /// or eleven hours early.
    /// </summary>
    [Fact]
    public void Today_is_the_date_in_the_declared_collection_zone()
    {
        // 23:00 UTC on 4 November 2026 is already 10:00 on the 5th in Sydney.
        var calendar = CalendarAt(new DateTime(2026, 11, 4, 23, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 11, 5), calendar.Today);
    }

    [Fact]
    public void Today_rolls_over_at_the_zones_own_midnight()
    {
        Assert.Equal(
            new DateOnly(2026, 11, 4),
            CalendarAt(new DateTime(2026, 11, 4, 12, 59, 59, DateTimeKind.Utc)).Today);

        Assert.Equal(
            new DateOnly(2026, 11, 5),
            CalendarAt(new DateTime(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc)).Today);
    }

    /// <summary>
    /// An ordinary daylight-saving date: Sydney is eleven hours ahead, so the day starts at 13:00 UTC
    /// the day before.
    /// </summary>
    [Fact]
    public void A_daylight_saving_date_starts_eleven_hours_before_utc_midnight()
    {
        Assert.Equal(
            new DateTime(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc),
            Calendar().StartOfDayUtc(new DateOnly(2026, 11, 5)));
    }

    /// <summary>An ordinary standard-time date: ten hours ahead.</summary>
    [Fact]
    public void A_standard_time_date_starts_ten_hours_before_utc_midnight()
    {
        Assert.Equal(
            new DateTime(2026, 6, 30, 14, 0, 0, DateTimeKind.Utc),
            Calendar().StartOfDayUtc(new DateOnly(2026, 7, 1)));
    }

    /// <summary>
    /// The day daylight saving <strong>starts</strong>, 4 October 2026: the clocks go forward at
    /// 02:00, so midnight is still standard time and the day starts at 14:00 UTC on the 3rd.
    /// </summary>
    [Fact]
    public void The_day_daylight_saving_starts_begins_at_standard_time_midnight()
    {
        Assert.Equal(
            new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc),
            Calendar().StartOfDayUtc(new DateOnly(2026, 10, 4)));

        // And the day after it is an ordinary daylight-saving day again.
        Assert.Equal(
            new DateTime(2026, 10, 4, 13, 0, 0, DateTimeKind.Utc),
            Calendar().StartOfDayUtc(new DateOnly(2026, 10, 5)));
    }

    /// <summary>
    /// The day daylight saving <strong>ends</strong>, 5 April 2026: the clocks go back at 03:00, so
    /// midnight is still daylight-saving time and the day starts at 13:00 UTC on the 4th. The hour
    /// between 13:00 and 14:00 UTC therefore belongs to the 5th here and to the previous day on the
    /// October date above, which is exactly why neither offset can be a constant.
    /// </summary>
    [Fact]
    public void The_day_daylight_saving_ends_begins_at_daylight_saving_midnight()
    {
        Assert.Equal(
            new DateTime(2026, 4, 4, 13, 0, 0, DateTimeKind.Utc),
            Calendar().StartOfDayUtc(new DateOnly(2026, 4, 5)));

        // And the day after it is an ordinary standard-time day.
        Assert.Equal(
            new DateTime(2026, 4, 5, 14, 0, 0, DateTimeKind.Utc),
            Calendar().StartOfDayUtc(new DateOnly(2026, 4, 6)));
    }

    /// <summary>
    /// Every day of a whole year starts exactly once and strictly after the day before it - which is
    /// what makes "the latest occurrence on or before today" a well-defined instant on all 365 of
    /// them, transitions included.
    /// </summary>
    [Fact]
    public void Every_day_of_a_year_starts_strictly_after_the_day_before_it()
    {
        var calendar = Calendar();
        var previous = calendar.StartOfDayUtc(new DateOnly(2026, 1, 1));

        for (var day = 1; day < 365; day++)
        {
            var start = calendar.StartOfDayUtc(new DateOnly(2026, 1, 1).AddDays(day));

            Assert.True(
                start > previous,
                $"The collection day beginning {new DateOnly(2026, 1, 1).AddDays(day):yyyy-MM-dd} "
                    + $"starts at {start:O}, which is not after the previous day's {previous:O}.");

            previous = start;
        }
    }

    /// <summary>
    /// An instant is dated in the collection zone too, which is what decides the day a price was
    /// observed on (issue #1200): 20:00 UTC on Wednesday 11 November 2026 is already 07:00 on the
    /// Thursday in Sydney, and reading the UTC date would move the observation a whole day - and with
    /// it the scheduled run that was meant to refresh it.
    /// </summary>
    [Fact]
    public void An_instant_is_dated_in_the_collection_zone()
    {
        var calendar = Calendar();

        Assert.Equal(
            new DateOnly(2026, 11, 12),
            calendar.DateOf(new DateTime(2026, 11, 11, 20, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(
            new DateOnly(2026, 11, 11),
            calendar.DateOf(new DateTime(2026, 11, 11, 12, 59, 59, DateTimeKind.Utc)));

        // And it is the same answer Today gives of the current instant.
        Assert.Equal(
            CalendarAt(new DateTime(2026, 11, 11, 20, 0, 0, DateTimeKind.Utc)).Today,
            calendar.DateOf(new DateTime(2026, 11, 11, 20, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The criterion: the freshness window is right across both Australia/Sydney daylight-saving
    /// changes, over the real zone rules rather than an offset somebody wrote down.
    ///
    /// <para>Daylight saving starts on Sunday 4 October 2026 and ends on Sunday 5 April 2026, and in
    /// both weeks a day is an hour shorter or longer than the one before it. What the rule has to
    /// produce either way is 06:00 Sydney on the morning after the supplier's next search day - which
    /// is 19:00 UTC the previous day in standard time and 20:00 in daylight-saving time - so a
    /// boundary built on a fixed offset would be an hour out for a week twice a year.</para>
    /// </summary>
    [Theory]
    // A daily supplier whose price was read on the Saturday before the clocks went forward: the next
    // search day is Sunday 4 October, which ends at 13:00 UTC, so the price is guidance until 19:00.
    [InlineData("2026-10-03T06:00:00Z", "2026-10-04T19:00:00Z")]
    // And one read on that Sunday itself: Monday 5 October ends at 13:00 UTC on the Monday.
    [InlineData("2026-10-04T06:00:00Z", "2026-10-05T19:00:00Z")]
    // The Saturday before the clocks went back: Sunday 5 April ends at 14:00 UTC, so 20:00.
    [InlineData("2026-04-04T06:00:00Z", "2026-04-05T20:00:00Z")]
    // And that Sunday itself: Monday 6 April ends at 14:00 UTC on the Monday.
    [InlineData("2026-04-05T06:00:00Z", "2026-04-06T20:00:00Z")]
    public void The_freshness_boundary_is_right_across_the_daylight_saving_changes(
        string observedAtUtc,
        string comparableUntilUtc)
    {
        var observed = DateTime.Parse(
            observedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        var window = SupplierSearchScheduleWindow.NextSearchDayEndUtc(
            Calendar(),
            SupplierSearchSchedulePolicy.Daily,
            observed);

        Assert.Equal(
            DateTime.Parse(
                comparableUntilUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            SupplierPriceFreshnessPolicy.ComparableUntilUtc(window));
    }

    /// <summary>
    /// The owner's own examples, end to end over the real zone (issue #1200): Coles on Wednesday and
    /// Sunday, BIG W every second Saturday, a daily supplier, and a Coles price read by a manual run
    /// on a Tuesday.
    /// </summary>
    [Fact]
    public void The_owners_examples_come_out_as_he_stated_them()
    {
        var colesWednesdayAndSunday = SupplierSearchSchedulePolicy.Require(
            new SupplierSearchSchedule(
                SupplierSearchFrequency.Weekly,
                [DayOfWeek.Wednesday, DayOfWeek.Sunday],
                null));

        // Checked on Wednesday 11 November 2026: current until Monday 16 November 06:00 Sydney.
        Assert.Equal(
            new DateTime(2026, 11, 15, 19, 0, 0, DateTimeKind.Utc),
            ComparableUntil(colesWednesdayAndSunday, new DateTime(2026, 11, 11, 6, 0, 0, DateTimeKind.Utc)));

        // Read by a manual run on Tuesday 10 November: Wednesday is the next run, so Thursday 06:00.
        Assert.Equal(
            new DateTime(2026, 11, 11, 19, 0, 0, DateTimeKind.Utc),
            ComparableUntil(colesWednesdayAndSunday, new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc)));

        // BIG W every second Saturday, the on weeks starting Monday 9 November: a price read on
        // Saturday 14 November is current until the Sunday 06:00 after the next on-week Saturday,
        // which is 29 November.
        var bigWFortnightly = SupplierSearchSchedulePolicy.Require(
            new SupplierSearchSchedule(
                SupplierSearchFrequency.Fortnightly,
                [DayOfWeek.Saturday],
                new DateOnly(2026, 11, 9)));

        Assert.Equal(
            new DateTime(2026, 11, 28, 19, 0, 0, DateTimeKind.Utc),
            ComparableUntil(bigWFortnightly, new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc)));

        // JB Metro daily, read at 05:39 Sydney on Thursday 12 November: current until Saturday 06:00.
        Assert.Equal(
            new DateTime(2026, 11, 13, 19, 0, 0, DateTimeKind.Utc),
            ComparableUntil(
                SupplierSearchSchedulePolicy.Daily,
                new DateTime(2026, 11, 11, 18, 39, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The collection zone is the one the Domain declares. Stated here so a change to that constant
    /// is a visible change to what a collection day means rather than a quiet one.
    /// </summary>
    [Fact]
    public void The_collection_zone_is_the_one_the_domain_declares()
    {
        Assert.Equal("Australia/Sydney", SupplierSearchSchedulePolicy.TimeZoneId);
    }

    /// <summary>
    /// When a price observed at <paramref name="observedAtUtc"/> stops being compared under
    /// <paramref name="schedule"/>: the window the calendar derives, plus the Domain's own margin.
    /// </summary>
    private static DateTime ComparableUntil(
        SupplierSearchSchedule schedule,
        DateTime observedAtUtc) =>
        SupplierPriceFreshnessPolicy.ComparableUntilUtc(
            SupplierSearchScheduleWindow.NextSearchDayEndUtc(
                Calendar(),
                schedule,
                observedAtUtc));

    private static SupplierSearchScheduleCalendar Calendar() =>
        CalendarAt(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));

    private static SupplierSearchScheduleCalendar CalendarAt(DateTime utcNow) =>
        new(new FakeClock(utcNow));
}
