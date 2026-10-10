using Inventory.Infrastructure.Time;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Time;

/// <summary>
/// The business-date conversion itself, in a zone the calendar is handed (issue #499; previously
/// <c>SydneyBusinessCalendarTests</c> over a hard-coded Sydney zone).
///
/// The Sydney cases are unchanged, and they are the regression guard that a business configured
/// for <c>Australia/Sydney</c> - which the application's existing business is - still gets exactly
/// the business days it always did. The <c>America/New_York</c> cases are the same assertions for a
/// business on the other side of the world and the other daylight-saving calendar, including both
/// of its transition days: a northern-hemisphere zone transitions in the opposite direction at the
/// opposite time of year, so a conversion that quietly assumed the Australian rules would pass the
/// Sydney cases and fail these.
/// </summary>
public class ZonedBusinessCalendarTests
{
    private const string Sydney = "Australia/Sydney";
    private const string NewYork = "America/New_York";

    private static ZonedBusinessCalendar CalendarFor(string timeZoneId) =>
        ZonedBusinessCalendar.ForTimeZone(new FakeClock(DateTime.UtcNow), timeZoneId);

    [Fact]
    public void Australia_Sydney_has_a_Windows_time_zone_mapping_for_cross_platform_fallback()
    {
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId(Sydney, out var windowsId));
        Assert.False(string.IsNullOrWhiteSpace(windowsId));
    }

    [Fact]
    public void America_New_York_has_a_Windows_time_zone_mapping_for_cross_platform_fallback()
    {
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId(NewYork, out var windowsId));
        Assert.False(string.IsNullOrWhiteSpace(windowsId));
    }

    [Theory]
    [InlineData(Sydney)]
    [InlineData(NewYork)]
    public void Calendar_constructs_with_the_platform_time_zone(string timeZoneId)
    {
        var exception = Record.Exception(() => CalendarFor(timeZoneId));

        Assert.Null(exception);
    }

    [Fact]
    public void ToBusinessDate_rolls_over_at_Sydney_midnight_during_standard_time()
    {
        var calendar = CalendarFor(Sydney);

        Assert.Equal(new DateTime(2026, 7, 14),
            calendar.ToBusinessDate(new DateTime(2026, 7, 14, 13, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 15),
            calendar.ToBusinessDate(new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ToBusinessDate_rolls_over_at_Sydney_midnight_during_daylight_saving()
    {
        var calendar = CalendarFor(Sydney);

        Assert.Equal(new DateTime(2026, 1, 14),
            calendar.ToBusinessDate(new DateTime(2026, 1, 14, 12, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 1, 15),
            calendar.ToBusinessDate(new DateTime(2026, 1, 14, 13, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_uses_AEST_offset_outside_daylight_saving()
    {
        var calendar = CalendarFor(Sydney);

        Assert.Equal(new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_uses_AEDT_offset_during_daylight_saving()
    {
        var calendar = CalendarFor(Sydney);

        Assert.Equal(new DateTime(2026, 1, 14, 13, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 1, 15)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_still_uses_AEST_on_the_day_daylight_saving_starts()
    {
        // 2026-10-04 is the day Sydney clocks jump forward from AEST to AEDT at 2am local;
        // midnight that day is still governed by the outgoing AEST (+10) offset.
        var calendar = CalendarFor(Sydney);

        Assert.Equal(new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 10, 4)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_still_uses_AEDT_on_the_day_daylight_saving_ends()
    {
        // 2026-04-05 is the day Sydney clocks fall back from AEDT to AEST at 3am local;
        // midnight that day is still governed by the outgoing AEDT (+11) offset.
        var calendar = CalendarFor(Sydney);

        Assert.Equal(new DateTime(2026, 4, 4, 13, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 4, 5)));
    }

    [Fact]
    public void Today_derives_the_Sydney_business_date_from_the_clock()
    {
        var clock = new FakeClock(new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc));
        var calendar = ZonedBusinessCalendar.ForTimeZone(clock, Sydney);

        Assert.Equal(new DateTime(2026, 7, 15), calendar.Today);
    }

    [Fact]
    public void ToBusinessDate_rolls_over_at_New_York_midnight_during_standard_time()
    {
        var calendar = CalendarFor(NewYork);

        // Eastern Standard Time is UTC-5, so a New York day begins at 05:00 UTC.
        Assert.Equal(new DateTime(2026, 1, 14),
            calendar.ToBusinessDate(new DateTime(2026, 1, 15, 4, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 1, 15),
            calendar.ToBusinessDate(new DateTime(2026, 1, 15, 5, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ToBusinessDate_rolls_over_at_New_York_midnight_during_daylight_saving()
    {
        var calendar = CalendarFor(NewYork);

        // Eastern Daylight Time is UTC-4, so the same instant falls on a different business date
        // than it does in standard time - and on a different one again than in Sydney.
        Assert.Equal(new DateTime(2026, 7, 14),
            calendar.ToBusinessDate(new DateTime(2026, 7, 15, 3, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 15),
            calendar.ToBusinessDate(new DateTime(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_uses_EST_offset_outside_daylight_saving()
    {
        var calendar = CalendarFor(NewYork);

        Assert.Equal(new DateTime(2026, 1, 15, 5, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 1, 15)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_uses_EDT_offset_during_daylight_saving()
    {
        var calendar = CalendarFor(NewYork);

        Assert.Equal(new DateTime(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_still_uses_EST_on_the_day_New_York_daylight_saving_starts()
    {
        // 2026-03-08 is the day New York clocks jump forward from EST to EDT at 2am local;
        // midnight that day is still governed by the outgoing EST (-5) offset. The transition is
        // in the opposite direction, and six months away, from Sydney's.
        var calendar = CalendarFor(NewYork);

        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 3, 8)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_still_uses_EDT_on_the_day_New_York_daylight_saving_ends()
    {
        // 2026-11-01 is the day New York clocks fall back from EDT to EST at 2am local;
        // midnight that day is still governed by the outgoing EDT (-4) offset, so the 25-hour day
        // is covered by this start and the next day's start as an exclusive upper bound.
        var calendar = CalendarFor(NewYork);

        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 11, 1)));
    }

    /// <summary>
    /// A business day's inclusive UTC window is <c>StartOfBusinessDayUtc(date)</c> up to, but not
    /// including, <c>StartOfBusinessDayUtc(date + 1)</c>, and that window must cover the whole day
    /// whatever happens to the clocks inside it: 23 hours when daylight saving starts, 25 when it
    /// ends. This is the property every report date filter depends on, checked in both zones and
    /// in both directions.
    /// </summary>
    [Theory]
    [InlineData(Sydney, 2026, 10, 4, 23)]
    [InlineData(Sydney, 2026, 4, 5, 25)]
    [InlineData(NewYork, 2026, 3, 8, 23)]
    [InlineData(NewYork, 2026, 11, 1, 25)]
    [InlineData(Sydney, 2026, 7, 15, 24)]
    [InlineData(NewYork, 2026, 7, 15, 24)]
    public void A_business_days_UTC_window_covers_exactly_that_day_across_a_transition(
        string timeZoneId, int year, int month, int day, int expectedHours)
    {
        var calendar = CalendarFor(timeZoneId);
        var businessDate = new DateTime(year, month, day);

        var start = calendar.StartOfBusinessDayUtc(businessDate);
        var nextStart = calendar.StartOfBusinessDayUtc(businessDate.AddDays(1));

        Assert.Equal(expectedHours, (nextStart - start).TotalHours);
        Assert.Equal(businessDate, calendar.ToBusinessDate(start));
        Assert.Equal(businessDate, calendar.ToBusinessDate(nextStart.AddMilliseconds(-1)));
        Assert.Equal(businessDate.AddDays(1), calendar.ToBusinessDate(nextStart));
    }

    /// <summary>
    /// Some real IANA zones move their clocks at midnight rather than at 2am, so the start of a
    /// business day is a real instant that a plain wall-clock conversion cannot name: midnight
    /// either never happens that day or happens twice. The zones below are synthetic so the
    /// assertion does not depend on which time-zone database version the host carries, but the
    /// rule they pin down is the one Africa/Cairo, Asia/Tehran and America/Havana need.
    /// </summary>
    [Fact]
    public void StartOfBusinessDayUtc_resolves_a_day_whose_midnight_is_skipped_by_a_transition()
    {
        // Clocks jump from 00:00 straight to 01:00 on 1 July 2026, so that day's midnight never
        // happens: the business day starts at the instant of the jump. Converting the wall-clock
        // midnight instead throws, which is what this guards against.
        var timeZone = TimeZoneWithMidnightSkipped();
        var calendar = new ZonedBusinessCalendar(new FakeClock(DateTime.UtcNow), timeZone);

        var start = calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 1));

        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 7, 1), calendar.ToBusinessDate(start));
    }

    [Fact]
    public void StartOfBusinessDayUtc_takes_the_first_of_two_midnights_when_a_transition_repeats_one()
    {
        // Clocks fall back from 01:00 to 00:00 on 1 July 2026, so that day's midnight happens
        // twice. The business day starts at the earlier of the two instants: starting at the later
        // one would leave the day's first hour of trading in no business day at all.
        var timeZone = TimeZoneWithMidnightRepeated();
        var calendar = new ZonedBusinessCalendar(new FakeClock(DateTime.UtcNow), timeZone);

        var start = calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 1));

        Assert.Equal(new DateTime(2026, 6, 30, 23, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 7, 1), calendar.ToBusinessDate(start));

        // The plain conversion resolves an ambiguous wall-clock time as standard time, which is
        // the second of the two midnights - an hour into the business day.
        Assert.Equal(
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(new DateTime(2026, 7, 1), DateTimeKind.Unspecified), timeZone));
    }

    /// <summary>A UTC-based zone whose clocks jump forward one hour at midnight on 1 July 2026.</summary>
    private static TimeZoneInfo TimeZoneWithMidnightSkipped() =>
        CustomTimeZone(
            "Test/MidnightSkipped",
            daylightStart: TimeZoneInfo.TransitionTime.CreateFixedDateRule(Midnight, 7, 1),
            daylightEnd: TimeZoneInfo.TransitionTime.CreateFixedDateRule(Midnight, 12, 1));

    /// <summary>
    /// A UTC-based zone whose clocks fall back one hour at 01:00 on 1 July 2026, which is what
    /// makes that day's midnight the repeated hour.
    /// </summary>
    private static TimeZoneInfo TimeZoneWithMidnightRepeated() =>
        CustomTimeZone(
            "Test/MidnightRepeated",
            daylightStart: TimeZoneInfo.TransitionTime.CreateFixedDateRule(Midnight, 2, 1),
            daylightEnd: TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 7, 1));

    private static readonly DateTime Midnight = new(1, 1, 1, 0, 0, 0);

    private static TimeZoneInfo CustomTimeZone(
        string id,
        TimeZoneInfo.TransitionTime daylightStart,
        TimeZoneInfo.TransitionTime daylightEnd) =>
        TimeZoneInfo.CreateCustomTimeZone(
            id,
            TimeSpan.Zero,
            id,
            $"{id} standard",
            $"{id} daylight",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2026, 1, 1),
                    new DateTime(2026, 12, 31),
                    TimeSpan.FromHours(1),
                    daylightStart,
                    daylightEnd),
            ]);
}
