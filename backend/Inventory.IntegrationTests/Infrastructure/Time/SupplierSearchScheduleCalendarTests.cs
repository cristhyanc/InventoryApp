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
    /// The collection zone is the one the Domain declares. Stated here so a change to that constant
    /// is a visible change to what a collection day means rather than a quiet one.
    /// </summary>
    [Fact]
    public void The_collection_zone_is_the_one_the_domain_declares()
    {
        Assert.Equal("Australia/Sydney", SupplierSearchSchedulePolicy.TimeZoneId);
    }

    private static SupplierSearchScheduleCalendar Calendar() =>
        CalendarAt(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));

    private static SupplierSearchScheduleCalendar CalendarAt(DateTime utcNow) =>
        new(new FakeClock(utcNow));
}
