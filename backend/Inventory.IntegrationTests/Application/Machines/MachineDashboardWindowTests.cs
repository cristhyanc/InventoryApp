using Inventory.Application.Machines;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// Issue #310: the machine/site dashboard window is derived from the <c>Australia/Sydney</c> business
/// day and returned as UTC instants, so the periods mean the business's day on a UTC host and still
/// compare directly against the UTC-persisted <c>MachineAuthorizationTime</c> sale instants. Every
/// expected instant below was taken from the IANA timezone database for Sydney (AEST +10, AEDT +11,
/// daylight saving ending at 03:00 AEDT on Sunday 5 April 2026 and starting at 02:00 AEST on Sunday
/// 4 October 2026), not from a fixed offset.
/// </summary>
public class MachineDashboardWindowTests
{
    /// <summary>
    /// 14:30 UTC on Wednesday 11 March 2026 is already 01:30 on Thursday 12 March in Sydney, so the
    /// dashboard's "today" is 12 March. A host-local read on a UTC App Service host would have
    /// started today at 11 March 00:00 UTC - more than a full day earlier - and counted the whole of
    /// the Sydney 11 March trading day as today.
    /// </summary>
    [Fact]
    public void Resolve_TakesTodayFromTheSydneyBusinessDay_NotTheHostsUtcDate()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 12), window.BusinessToday);
        Assert.Equal(time.NowUtc, window.NowUtc);
        Assert.Equal(Utc(2026, 3, 11, 13, 0), window.Today.StartUtc);
        Assert.Equal(time.NowUtc, window.Today.EndUtc);
    }

    /// <summary>
    /// Thursday 12 March 2026 in Sydney: the week to date starts at Monday 9 March midnight Sydney,
    /// the previous comparable week starts a week earlier and ends the same distance into that week,
    /// last week and two weeks ago are complete Monday-to-Sunday weeks ending one millisecond before
    /// the following Sydney week begins, and the month to date starts at 1 March midnight Sydney.
    /// </summary>
    [Fact]
    public void Resolve_DerivesEveryPeriodBoundaryFromSydneyMidnight()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(Utc(2026, 3, 8, 13, 0), window.CurrentWeek.StartUtc);
        Assert.Equal(time.NowUtc, window.CurrentWeek.EndUtc);

        Assert.Equal(Utc(2026, 3, 1, 13, 0), window.PreviousComparableWeek.StartUtc);
        Assert.Equal(time.NowUtc.AddDays(-7), window.PreviousComparableWeek.EndUtc);

        Assert.Equal(Utc(2026, 3, 1, 13, 0), window.LastWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 8, 13, 0).AddMilliseconds(-1), window.LastWeek.EndUtc);

        Assert.Equal(Utc(2026, 2, 22, 13, 0), window.TwoWeeksAgo.StartUtc);
        Assert.Equal(Utc(2026, 3, 1, 13, 0).AddMilliseconds(-1), window.TwoWeeksAgo.EndUtc);

        Assert.Equal(Utc(2026, 2, 28, 13, 0), window.MonthToDate.StartUtc);
        Assert.Equal(time.NowUtc, window.MonthToDate.EndUtc);
    }

    /// <summary>
    /// Daylight saving ends on Sunday 5 April 2026, so that Sydney day is 25 hours long: it starts at
    /// 13:00 UTC on 4 April (AEDT, +11) and the instant 13:30 UTC on 5 April is still the same Sydney
    /// day at 23:30 (AEST, +10). A fixed +10 offset would start today an hour late and a fixed +11
    /// would place this instant on 6 April.
    /// </summary>
    [Fact]
    public void Resolve_HandlesTheEndOfDaylightSaving()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 4, 5, 13, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 4, 5), window.BusinessToday);
        Assert.Equal(Utc(2026, 4, 4, 13, 0), window.Today.StartUtc);

        // Sunday 5 April is the last day of its own business week, which started on Monday 30 March.
        Assert.Equal(Utc(2026, 3, 29, 13, 0), window.CurrentWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 22, 13, 0), window.LastWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 29, 13, 0).AddMilliseconds(-1), window.LastWeek.EndUtc);
        Assert.Equal(Utc(2026, 3, 31, 13, 0), window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// Daylight saving starts on Sunday 4 October 2026, so that Sydney day begins at 14:00 UTC on
    /// 3 October (AEST, +10) and is only 23 hours long. One minute earlier is still 3 October in
    /// Sydney; a fixed +11 offset would have moved the business day forward a day too early.
    /// </summary>
    [Theory]
    [InlineData(13, 59, 2026, 10, 3, 2026, 10, 2, 14, 0)]
    [InlineData(14, 0, 2026, 10, 4, 2026, 10, 3, 14, 0)]
    public void Resolve_HandlesTheStartOfDaylightSaving(
        int utcHour,
        int utcMinute,
        int expectedBusinessYear,
        int expectedBusinessMonth,
        int expectedBusinessDay,
        int expectedStartYear,
        int expectedStartMonth,
        int expectedStartDay,
        int expectedStartHour,
        int expectedStartMinute)
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 3, utcHour, utcMinute, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(
            new DateTime(expectedBusinessYear, expectedBusinessMonth, expectedBusinessDay),
            window.BusinessToday);
        Assert.Equal(
            Utc(expectedStartYear, expectedStartMonth, expectedStartDay, expectedStartHour, expectedStartMinute),
            window.Today.StartUtc);
    }

    /// <summary>
    /// Sunday 4 October 2026 is the last day of the business week that started on Monday 28 September,
    /// before daylight saving began, so that week's start is an AEST (+10) midnight while the current
    /// day's periods either side of the transition are not.
    /// </summary>
    [Fact]
    public void Resolve_KeepsAWeekThatSpansTheDaylightSavingTransitionOnItsOwnBoundaries()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(Utc(2026, 9, 27, 14, 0), window.CurrentWeek.StartUtc);
        Assert.Equal(Utc(2026, 9, 20, 14, 0), window.LastWeek.StartUtc);
        Assert.Equal(Utc(2026, 9, 27, 14, 0).AddMilliseconds(-1), window.LastWeek.EndUtc);
        Assert.Equal(Utc(2026, 9, 30, 14, 0), window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// The Monday after daylight saving starts. 13:30 UTC on Sunday 4 October 2026 is 00:30 on Monday
    /// 5 October in Sydney, half an hour into a business week whose AEDT (+11) Monday midnight is
    /// 4 October 13:00 UTC, while the previous week's AEST (+10) Monday midnight is 27 September
    /// 14:00 UTC. The comparable period must therefore end half an hour into *that* week. Subtracting
    /// seven days from the current UTC instant instead lands at 27 September 13:30 UTC - thirty
    /// minutes before its own start - and reports an empty previous week.
    /// </summary>
    [Fact]
    public void Resolve_KeepsThePreviousComparableWeekComparable_OnTheMondayAfterDaylightSavingStarts()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 4, 13, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 10, 5), window.BusinessToday);
        Assert.Equal(Utc(2026, 10, 4, 13, 0), window.CurrentWeek.StartUtc);
        Assert.Equal(Utc(2026, 9, 27, 14, 0), window.PreviousComparableWeek.StartUtc);
        Assert.Equal(Utc(2026, 9, 27, 14, 30), window.PreviousComparableWeek.EndUtc);
        Assert.True(window.PreviousComparableWeek.EndUtc > window.PreviousComparableWeek.StartUtc);
    }

    /// <summary>
    /// The Monday after daylight saving ends, the mirror image of the case above: 14:30 UTC on Sunday
    /// 5 April 2026 is 00:30 on Monday 6 April in Sydney (AEST, +10), and the previous week's Monday
    /// midnight was an AEDT (+11) one at 29 March 13:00 UTC, so the comparable period ends at
    /// 29 March 13:30 UTC - 00:30 on Monday 30 March in Sydney, the same half hour into its week.
    /// </summary>
    [Fact]
    public void Resolve_KeepsThePreviousComparableWeekComparable_OnTheMondayAfterDaylightSavingEnds()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 4, 5, 14, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 4, 6), window.BusinessToday);
        Assert.Equal(Utc(2026, 4, 5, 14, 0), window.CurrentWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 29, 13, 0), window.PreviousComparableWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 29, 13, 30), window.PreviousComparableWeek.EndUtc);
        Assert.True(window.PreviousComparableWeek.EndUtc > window.PreviousComparableWeek.StartUtc);
    }

    /// <summary>
    /// 23:30 on Sunday 5 April 2026 in Sydney is 169 hours into its own business week, because
    /// daylight saving ended inside it and made that week an hour longer than the one before. The
    /// comparable period is held at the end of the previous week rather than extended into the
    /// current one, so the two periods can never count the same sale twice.
    /// </summary>
    [Fact]
    public void Resolve_NeverExtendsThePreviousComparableWeekIntoTheCurrentWeek()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 4, 5, 13, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(Utc(2026, 3, 29, 13, 0), window.CurrentWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 22, 13, 0), window.PreviousComparableWeek.StartUtc);
        Assert.Equal(Utc(2026, 3, 29, 13, 0).AddMilliseconds(-1), window.PreviousComparableWeek.EndUtc);
        Assert.True(window.PreviousComparableWeek.EndUtc < window.CurrentWeek.StartUtc);
    }

    /// <summary>
    /// Every period also carries the <c>Australia/Sydney</c> business dates it covers, because the
    /// Nayax processing fee engine charges fees by business date: those dates must describe exactly the
    /// same period as the UTC instants beside them, or a period's profit would subtract a fee for a day
    /// whose revenue it does not count (issue #310).
    /// </summary>
    [Fact]
    public void Resolve_CarriesTheBusinessDatesEachPeriodCovers()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal((new DateTime(2026, 3, 12), new DateTime(2026, 3, 12)), BusinessDates(window.Today));
        Assert.Equal((new DateTime(2026, 3, 9), new DateTime(2026, 3, 12)), BusinessDates(window.CurrentWeek));
        Assert.Equal((new DateTime(2026, 3, 2), new DateTime(2026, 3, 5)), BusinessDates(window.PreviousComparableWeek));
        Assert.Equal((new DateTime(2026, 3, 2), new DateTime(2026, 3, 8)), BusinessDates(window.LastWeek));
        Assert.Equal((new DateTime(2026, 3, 1), new DateTime(2026, 3, 12)), BusinessDates(window.MonthToDate));
        Assert.Equal((new DateTime(2026, 2, 23), new DateTime(2026, 3, 1)), BusinessDates(window.TwoWeeksAgo));

        // Each period's own instants fall on the first and last business date it claims to cover.
        MachineDashboardPeriodUtc[] periods =
        [
            window.Today, window.CurrentWeek, window.PreviousComparableWeek,
            window.LastWeek, window.MonthToDate, window.TwoWeeksAgo
        ];
        Assert.All(periods, period =>
        {
            Assert.Equal(period.FirstBusinessDate, time.Calendar.ToBusinessDate(period.StartUtc));
            Assert.Equal(period.LastBusinessDate, time.Calendar.ToBusinessDate(period.EndUtc));
        });
    }

    private static (DateTime First, DateTime Last) BusinessDates(MachineDashboardPeriodUtc period) =>
        (period.FirstBusinessDate, period.LastBusinessDate);

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
