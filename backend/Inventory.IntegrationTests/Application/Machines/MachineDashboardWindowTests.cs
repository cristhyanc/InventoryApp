using Inventory.Application.Machines;
using Inventory.Infrastructure.Time;
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
    /// The previous comparable day (issue #1177): yesterday's business day up to the same elapsed
    /// time since business-local midnight as now is into today's. 15:00 on Thursday 12 March 2026 in
    /// Sydney is fifteen hours into today, so the baseline runs from Wednesday 11 March's own
    /// midnight (10 March 13:00 UTC) to fifteen hours into it, and both its business dates are
    /// 11 March.
    /// </summary>
    [Fact]
    public void Resolve_EndsThePreviousComparableDayTheSameElapsedTimeIntoYesterday()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 12, 4, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 12), window.BusinessToday);
        Assert.Equal(Utc(2026, 3, 10, 13, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(Utc(2026, 3, 11, 4, 0), window.PreviousComparableDay.EndUtc);
        Assert.Equal(new DateTime(2026, 3, 11), window.PreviousComparableDay.FirstBusinessDate);
        Assert.Equal(new DateTime(2026, 3, 11), window.PreviousComparableDay.LastBusinessDate);

        // The baseline is exactly as far into its day as today is into its own, from the one
        // captured instant every other period is measured from.
        Assert.Equal(
            window.NowUtc - window.Today.StartUtc,
            window.PreviousComparableDay.EndUtc - window.PreviousComparableDay.StartUtc);
        Assert.True(window.PreviousComparableDay.EndUtc < window.Today.StartUtc);
    }

    /// <summary>
    /// One minute into Thursday 12 March in Sydney: the baseline is the first minute of Wednesday
    /// 11 March and nothing more. A comparison that reached further would show yesterday's whole
    /// trading evening against today's first minute.
    /// </summary>
    [Fact]
    public void Resolve_KeepsThePreviousComparableDayShortJustAfterBusinessMidnight()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 11, 13, 1, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 12), window.BusinessToday);
        Assert.Equal(Utc(2026, 3, 10, 13, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(Utc(2026, 3, 10, 13, 1), window.PreviousComparableDay.EndUtc);
    }

    /// <summary>
    /// On a Monday, yesterday is the Sunday that closed the <em>previous</em> business week, so the
    /// baseline lies entirely inside last week and entirely before the current one. The day is read
    /// from the business calendar rather than from a week's arithmetic, so a week boundary does not
    /// shorten or move it.
    /// </summary>
    [Fact]
    public void Resolve_TakesThePreviousComparableDayFromLastWeekOnAMonday()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 8, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 9), window.BusinessToday);
        Assert.Equal(DayOfWeek.Monday, window.BusinessToday.DayOfWeek);
        Assert.Equal(Utc(2026, 3, 7, 13, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(Utc(2026, 3, 7, 23, 0), window.PreviousComparableDay.EndUtc);
        Assert.Equal(new DateTime(2026, 3, 8), window.PreviousComparableDay.FirstBusinessDate);

        // Sunday 8 March closed last week, which is where the baseline sits in full.
        Assert.True(window.PreviousComparableDay.EndUtc < window.CurrentWeek.StartUtc);
        Assert.True(window.PreviousComparableDay.StartUtc >= window.LastWeek.StartUtc);
        Assert.True(window.PreviousComparableDay.EndUtc <= window.LastWeek.EndUtc);
    }

    /// <summary>
    /// On the first of a month the baseline is in the month before, so it lies entirely before month
    /// to date begins; on 1 January it is in the year before as well. Neither is a special case in
    /// the arithmetic - the day comes from the business date minus one day - and both are pinned so
    /// that it stays that way.
    /// </summary>
    [Theory]
    [InlineData(2026, 2, 28, 23, 0, 2026, 3, 1, 2026, 2, 27, 13, 0, 2026, 2, 27, 23, 0, 2026, 2, 28)]
    [InlineData(2025, 12, 31, 23, 0, 2026, 1, 1, 2025, 12, 30, 13, 0, 2025, 12, 30, 23, 0, 2025, 12, 31)]
    public void Resolve_PlacesThePreviousComparableDayInThePreviousMonthOnTheFirst(
        int utcYear,
        int utcMonth,
        int utcDay,
        int utcHour,
        int utcMinute,
        int expectedBusinessYear,
        int expectedBusinessMonth,
        int expectedBusinessDay,
        int expectedStartYear,
        int expectedStartMonth,
        int expectedStartDay,
        int expectedStartHour,
        int expectedStartMinute,
        int expectedEndYear,
        int expectedEndMonth,
        int expectedEndDay,
        int expectedEndHour,
        int expectedEndMinute,
        int expectedBaselineDateYear,
        int expectedBaselineDateMonth,
        int expectedBaselineDateDay)
    {
        var time = new FixedSydneyTime(
            new DateTime(utcYear, utcMonth, utcDay, utcHour, utcMinute, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(
            new DateTime(expectedBusinessYear, expectedBusinessMonth, expectedBusinessDay),
            window.BusinessToday);
        Assert.Equal(
            Utc(expectedStartYear, expectedStartMonth, expectedStartDay, expectedStartHour, expectedStartMinute),
            window.PreviousComparableDay.StartUtc);
        Assert.Equal(
            Utc(expectedEndYear, expectedEndMonth, expectedEndDay, expectedEndHour, expectedEndMinute),
            window.PreviousComparableDay.EndUtc);
        Assert.Equal(
            new DateTime(expectedBaselineDateYear, expectedBaselineDateMonth, expectedBaselineDateDay),
            window.PreviousComparableDay.FirstBusinessDate);

        // Month to date began today, so the baseline is entirely outside it.
        Assert.Equal(window.Today.StartUtc, window.MonthToDate.StartUtc);
        Assert.True(window.PreviousComparableDay.EndUtc < window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// The day after daylight saving starts. Sunday 4 October 2026 is only 23 hours long, so at
    /// 23:30 on Monday 5 October - 23 and a half hours into today - the same elapsed time would land
    /// half an hour <em>inside</em> today. The baseline is held one millisecond before today begins,
    /// so it covers yesterday in full and can never count a sale today also counts.
    /// </summary>
    [Fact]
    public void Resolve_ClampsThePreviousComparableDayToYesterdayAfterDaylightSavingStarts()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 10, 5), window.BusinessToday);
        Assert.Equal(Utc(2026, 10, 3, 14, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(Utc(2026, 10, 4, 13, 0).AddMilliseconds(-1), window.PreviousComparableDay.EndUtc);
        Assert.Equal(window.Today.StartUtc.AddMilliseconds(-1), window.PreviousComparableDay.EndUtc);
        Assert.True(window.PreviousComparableDay.EndUtc < window.Today.StartUtc);

        // Clamped, so the baseline is shorter than the time elapsed into today: 23 hours of
        // yesterday against 23 and a half hours of today.
        Assert.True(
            window.PreviousComparableDay.EndUtc - window.PreviousComparableDay.StartUtc <
            window.NowUtc - window.Today.StartUtc);

        // Still the whole of yesterday, whose 23 hours are all the business day holds.
        Assert.Equal(new DateTime(2026, 10, 4), window.PreviousComparableDay.FirstBusinessDate);
        Assert.Equal(new DateTime(2026, 10, 4), window.PreviousComparableDay.LastBusinessDate);
    }

    /// <summary>
    /// Earlier on that same Monday the clamp does not apply: ten hours into today is ten hours into
    /// the 23-hour Sunday before it, measured from that day's own AEST (+10) midnight rather than by
    /// subtracting 24 hours from now, which would land an hour inside Saturday.
    /// </summary>
    [Fact]
    public void Resolve_MeasuresThePreviousComparableDayFromItsOwnMidnightAcrossATransition()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 4, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 10, 5), window.BusinessToday);
        Assert.Equal(Utc(2026, 10, 3, 14, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(Utc(2026, 10, 4, 0, 0), window.PreviousComparableDay.EndUtc);
        Assert.Equal(
            window.NowUtc - window.Today.StartUtc,
            window.PreviousComparableDay.EndUtc - window.PreviousComparableDay.StartUtc);
    }

    /// <summary>
    /// The clamp also holds on the day daylight saving <em>ends</em>, where today is the longer day:
    /// 23:30 on Sunday 5 April 2026 is 24 and a half hours into a 25-hour Sunday, and Saturday
    /// 4 April holds only 24. The baseline is held at Saturday's own last instant rather than
    /// reaching into Sunday.
    /// </summary>
    [Fact]
    public void Resolve_ClampsThePreviousComparableDayWhenTodayIsTheLongerDay()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 4, 5, 13, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 4, 5), window.BusinessToday);
        Assert.Equal(Utc(2026, 4, 3, 13, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(window.Today.StartUtc.AddMilliseconds(-1), window.PreviousComparableDay.EndUtc);
        Assert.Equal(Utc(2026, 4, 4, 13, 0).AddMilliseconds(-1), window.PreviousComparableDay.EndUtc);
        Assert.Equal(new DateTime(2026, 4, 4), window.PreviousComparableDay.LastBusinessDate);
    }

    /// <summary>
    /// The day after daylight saving ends, the mirror image: Sunday 5 April 2026 is 25 hours long,
    /// so the same elapsed time into it never reaches Monday 6 April and the clamp never applies -
    /// even at 23:30 on the Monday, where 23 and a half hours into a 25-hour day is 22:30 Sydney on
    /// the Sunday. The hour of difference on a transition day is the recorded decision: "the same
    /// time yesterday" is the same elapsed time since business-local midnight.
    /// </summary>
    [Fact]
    public void Resolve_NeverClampsThePreviousComparableDayAfterDaylightSavingEnds()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 4, 6, 13, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 4, 6), window.BusinessToday);
        Assert.Equal(Utc(2026, 4, 5, 14, 0), window.Today.StartUtc);
        Assert.Equal(Utc(2026, 4, 4, 13, 0), window.PreviousComparableDay.StartUtc);
        Assert.Equal(Utc(2026, 4, 5, 12, 30), window.PreviousComparableDay.EndUtc);
        Assert.True(window.PreviousComparableDay.EndUtc < window.Today.StartUtc);
        Assert.Equal(
            window.NowUtc - window.Today.StartUtc,
            window.PreviousComparableDay.EndUtc - window.PreviousComparableDay.StartUtc);
        Assert.Equal(new DateTime(2026, 4, 5), window.PreviousComparableDay.LastBusinessDate);
    }

    /// <summary>
    /// The whole previous day (issue #1215): yesterday from its own business-local midnight to one
    /// tick before today's - the last representable instant before it - whatever the time is now.
    /// At 15:00 on Thursday 12 March 2026 in
    /// Sydney that is all of Wednesday 11 March, so it reaches past the same-time baseline - which
    /// keeps its own meaning - and ends exactly where today begins.
    /// </summary>
    [Fact]
    public void Resolve_CoversTheWholePreviousDayWhateverTheTimeNow()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 12, 4, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(Utc(2026, 3, 10, 13, 0), window.PreviousDay.StartUtc);
        Assert.Equal(Utc(2026, 3, 11, 13, 0).AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(window.Today.StartUtc.AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(new DateTime(2026, 3, 11), window.PreviousDay.FirstBusinessDate);
        Assert.Equal(new DateTime(2026, 3, 11), window.PreviousDay.LastBusinessDate);

        // Same start as the same-time baseline, which is unchanged and ends fifteen hours in.
        Assert.Equal(window.PreviousComparableDay.StartUtc, window.PreviousDay.StartUtc);
        Assert.Equal(Utc(2026, 3, 11, 4, 0), window.PreviousComparableDay.EndUtc);
        Assert.True(window.PreviousDay.EndUtc > window.PreviousComparableDay.EndUtc);
    }

    /// <summary>
    /// One minute into Thursday 12 March in Sydney the whole previous day is still all of
    /// Wednesday 11 March: it does not shrink with the time elapsed into today.
    /// </summary>
    [Fact]
    public void Resolve_KeepsTheWholePreviousDayJustAfterBusinessMidnight()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 11, 13, 1, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 12), window.BusinessToday);
        Assert.Equal(Utc(2026, 3, 10, 13, 0), window.PreviousDay.StartUtc);
        Assert.Equal(Utc(2026, 3, 11, 13, 0).AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(Utc(2026, 3, 10, 13, 1), window.PreviousComparableDay.EndUtc);
    }

    /// <summary>
    /// The day after daylight saving starts in Sydney: Sunday 4 October 2026 is 23 hours long, from
    /// its AEST (+10) midnight to Monday's AEDT (+11) midnight, and the whole previous day is exactly
    /// those 23 hours.
    /// </summary>
    [Fact]
    public void Resolve_CoversA23HourPreviousDayAfterDaylightSavingStarts()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 4, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 10, 5), window.BusinessToday);
        Assert.Equal(Utc(2026, 10, 3, 14, 0), window.PreviousDay.StartUtc);
        Assert.Equal(Utc(2026, 10, 4, 13, 0).AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(
            TimeSpan.FromHours(23) - TimeSpan.FromTicks(1),
            window.PreviousDay.EndUtc - window.PreviousDay.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 4), window.PreviousDay.LastBusinessDate);
    }

    /// <summary>
    /// The day after daylight saving ends in Sydney: Sunday 5 April 2026 is 25 hours long, from its
    /// AEDT (+11) midnight to Monday's AEST (+10) midnight, and the whole previous day is all 25.
    /// </summary>
    [Fact]
    public void Resolve_CoversA25HourPreviousDayAfterDaylightSavingEnds()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 4, 5, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 4, 6), window.BusinessToday);
        Assert.Equal(Utc(2026, 4, 4, 13, 0), window.PreviousDay.StartUtc);
        Assert.Equal(Utc(2026, 4, 5, 14, 0).AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(
            TimeSpan.FromHours(25) - TimeSpan.FromTicks(1),
            window.PreviousDay.EndUtc - window.PreviousDay.StartUtc);
        Assert.Equal(new DateTime(2026, 4, 5), window.PreviousDay.LastBusinessDate);
    }

    /// <summary>
    /// The same rule in a second zone (AGENTS.md: timezone behaviour is tested in two zones). In
    /// <c>America/New_York</c> daylight saving starts at 02:00 EST on Sunday 8 March 2026, so on
    /// Monday 9 March the whole previous day runs from Sunday's EST (-5) midnight to Monday's EDT
    /// (-4) midnight - 23 hours - and on Monday 2 November, after it ends on Sunday 1 November, the
    /// previous day runs from Sunday's EDT midnight to Monday's EST midnight - 25 hours.
    /// </summary>
    [Theory]
    [InlineData(2026, 3, 9, 15, 2026, 3, 8, 5, 2026, 3, 9, 4, 23)]
    [InlineData(2026, 11, 2, 15, 2026, 11, 1, 4, 2026, 11, 2, 5, 25)]
    public void Resolve_CoversTheWholePreviousDayInAnotherZoneAcrossDaylightSaving(
        int nowYear, int nowMonth, int nowDay, int nowHour,
        int startYear, int startMonth, int startDay, int startHour,
        int endYear, int endMonth, int endDay, int endHour,
        int expectedHours)
    {
        var clock = new FakeClock(new DateTime(nowYear, nowMonth, nowDay, nowHour, 0, 0, DateTimeKind.Utc));
        var calendar = ZonedBusinessCalendar.ForTimeZone(clock, "America/New_York");

        var window = MachineDashboardWindow.Resolve(clock, calendar);

        Assert.Equal(new DateTime(nowYear, nowMonth, nowDay), window.BusinessToday);
        Assert.Equal(Utc(startYear, startMonth, startDay, startHour, 0), window.PreviousDay.StartUtc);
        Assert.Equal(Utc(endYear, endMonth, endDay, endHour, 0).AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(window.Today.StartUtc.AddTicks(-1), window.PreviousDay.EndUtc);
        Assert.Equal(
            TimeSpan.FromHours(expectedHours) - TimeSpan.FromTicks(1),
            window.PreviousDay.EndUtc - window.PreviousDay.StartUtc);
        Assert.Equal(new DateTime(startYear, startMonth, startDay), window.PreviousDay.FirstBusinessDate);
        Assert.Equal(new DateTime(startYear, startMonth, startDay), window.PreviousDay.LastBusinessDate);
    }

    /// <summary>
    /// The previous comparable month (issue #1180): the previous calendar month from its own first
    /// business-day midnight to the same point into the comparable day as now is into today. 01:30 on
    /// Thursday 12 March 2026 in Sydney is ninety minutes into today, so the baseline runs from
    /// 1 February's own midnight (31 January 13:00 UTC) to 01:30 on Thursday 12 February
    /// (11 February 14:30 UTC), and its business dates are 1 and 12 February.
    /// </summary>
    [Fact]
    public void Resolve_EndsThePreviousComparableMonthTheSamePointIntoTheComparableDay()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 12), window.BusinessToday);
        Assert.Equal(Utc(2026, 1, 31, 13, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2026, 2, 11, 14, 30), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(new DateTime(2026, 2, 1), window.PreviousComparableMonth.FirstBusinessDate);
        Assert.Equal(new DateTime(2026, 2, 12), window.PreviousComparableMonth.LastBusinessDate);

        // As far into the comparable day as today is into its own, from the one captured instant,
        // and entirely before this month began - so no sale can be in both figures.
        Assert.Equal(
            window.NowUtc - window.Today.StartUtc,
            window.PreviousComparableMonth.EndUtc - Utc(2026, 2, 11, 13, 0));
        Assert.True(window.PreviousComparableMonth.EndUtc < window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// One minute into the first of a month: the baseline is the first minute of the previous month's
    /// first day and nothing more, which is the month's mirror image of the day baseline's own
    /// just-after-midnight case. A comparison that reached further would show a whole month against
    /// one minute.
    /// </summary>
    [Fact]
    public void Resolve_KeepsThePreviousComparableMonthShortOnTheFirstJustAfterBusinessMidnight()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 2, 28, 13, 1, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 3, 1), window.BusinessToday);
        Assert.Equal(Utc(2026, 1, 31, 13, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2026, 1, 31, 13, 1), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(new DateTime(2026, 2, 1), window.PreviousComparableMonth.FirstBusinessDate);
        Assert.Equal(new DateTime(2026, 2, 1), window.PreviousComparableMonth.LastBusinessDate);

        // Month to date began today, so the baseline lies entirely outside it.
        Assert.Equal(window.Today.StartUtc, window.MonthToDate.StartUtc);
        Assert.True(window.PreviousComparableMonth.EndUtc < window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// On 1 January the previous month is December of the previous year, which is not a special case
    /// in the arithmetic - the previous calendar month is derived from the business date - and is
    /// pinned so that it stays that way. 10:00 on Thursday 1 January 2026 in Sydney is ten hours into
    /// today, so the baseline runs from 1 December 2025's own midnight to ten hours into that same
    /// day.
    /// </summary>
    [Fact]
    public void Resolve_PlacesThePreviousComparableMonthInThePreviousYearOnTheFirstOfJanuary()
    {
        var time = new FixedSydneyTime(new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 1, 1), window.BusinessToday);
        Assert.Equal(Utc(2025, 11, 30, 13, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2025, 11, 30, 23, 0), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(new DateTime(2025, 12, 1), window.PreviousComparableMonth.FirstBusinessDate);
        Assert.Equal(new DateTime(2025, 12, 1), window.PreviousComparableMonth.LastBusinessDate);
    }

    /// <summary>
    /// A day of the month the previous month does not have: the baseline is held at that month's own
    /// last instant - one millisecond before this month began - rather than reaching into this month
    /// for the missing days. February is the case that matters, in a non-leap year (28 days, so the
    /// 29th, 30th and 31st of March are all clamped) and in a leap year (29 days, so only the 30th
    /// and 31st are), and April's thirty days clamp the 31st of May the same way.
    /// </summary>
    [Theory]
    // 10:00 on 29, 30 and 31 March 2026 in Sydney (AEDT): February 2026 has 28 days.
    [InlineData(2026, 3, 28, 23, 0, 2026, 3, 29, 2026, 2, 28)]
    [InlineData(2026, 3, 29, 23, 0, 2026, 3, 30, 2026, 2, 28)]
    [InlineData(2026, 3, 30, 23, 0, 2026, 3, 31, 2026, 2, 28)]
    // 10:00 on 30 and 31 March 2028 in Sydney: February 2028 is a leap February, so its last day is
    // the 29th and the 29th of March is not clamped at all (asserted separately below).
    [InlineData(2028, 3, 29, 23, 0, 2028, 3, 30, 2028, 2, 29)]
    [InlineData(2028, 3, 30, 23, 0, 2028, 3, 31, 2028, 2, 29)]
    // 09:00 on 31 May 2026 in Sydney (AEST): April has 30 days.
    [InlineData(2026, 5, 30, 23, 0, 2026, 5, 31, 2026, 4, 30)]
    public void Resolve_HoldsThePreviousComparableMonthAtItsOwnEndWhenThatMonthIsShorter(
        int utcYear,
        int utcMonth,
        int utcDay,
        int utcHour,
        int utcMinute,
        int expectedBusinessYear,
        int expectedBusinessMonth,
        int expectedBusinessDay,
        int expectedLastBusinessYear,
        int expectedLastBusinessMonth,
        int expectedLastBusinessDay)
    {
        var time = new FixedSydneyTime(
            new DateTime(utcYear, utcMonth, utcDay, utcHour, utcMinute, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(
            new DateTime(expectedBusinessYear, expectedBusinessMonth, expectedBusinessDay),
            window.BusinessToday);

        // One millisecond before this month began, which is the previous month's own last instant:
        // the baseline covers that month in full and never a moment of this one.
        Assert.Equal(
            window.MonthToDate.StartUtc.AddMilliseconds(-1), window.PreviousComparableMonth.EndUtc);
        Assert.True(window.PreviousComparableMonth.EndUtc < window.MonthToDate.StartUtc);
        Assert.Equal(
            new DateTime(expectedLastBusinessYear, expectedLastBusinessMonth, expectedLastBusinessDay),
            window.PreviousComparableMonth.LastBusinessDate);
        Assert.Equal(
            new DateTime(expectedLastBusinessYear, expectedLastBusinessMonth, 1),
            window.PreviousComparableMonth.FirstBusinessDate);
    }

    /// <summary>
    /// The leap-year boundary the clamp above does not cross: 29 February 2028 exists, so 29 March
    /// 2028 compares against it by elapsed time like any other day and is not held at the month's
    /// end. In a non-leap year the same day of the month is clamped, which is the case pinned above.
    /// </summary>
    [Fact]
    public void Resolve_ComparesTheTwentyNinthWithALeapFebruary()
    {
        var time = new FixedSydneyTime(new DateTime(2028, 3, 28, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2028, 3, 29), window.BusinessToday);
        Assert.Equal(Utc(2028, 1, 31, 13, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2028, 2, 28, 23, 0), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(new DateTime(2028, 2, 29), window.PreviousComparableMonth.LastBusinessDate);
        Assert.True(window.PreviousComparableMonth.EndUtc < window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// A previous month containing a daylight-saving change, with the comparable day on the change
    /// day itself. Daylight saving starts on Sunday 4 October 2026, so that Sydney day is 23 hours
    /// long while Wednesday 4 November is 24: at 23:30 on 4 November the same elapsed time would land
    /// half an hour <em>inside</em> 5 October, so the baseline is held one millisecond before that day
    /// begins. The month's own first instant is an AEST (+10) midnight while today's is an AEDT (+11)
    /// one, which a fixed offset or a shifted UTC instant gets wrong.
    /// </summary>
    [Fact]
    public void Resolve_ClampsThePreviousComparableMonthToTheComparableDayAcrossADaylightSavingChange()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 11, 4, 12, 30, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 11, 4), window.BusinessToday);
        Assert.Equal(Utc(2026, 9, 30, 14, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2026, 10, 4, 13, 0).AddMilliseconds(-1), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(new DateTime(2026, 10, 1), window.PreviousComparableMonth.FirstBusinessDate);
        Assert.Equal(new DateTime(2026, 10, 4), window.PreviousComparableMonth.LastBusinessDate);

        // Never past the comparable day's own end, and never past the previous month's end either.
        Assert.True(window.PreviousComparableMonth.EndUtc < window.MonthToDate.StartUtc);
        Assert.True(
            window.PreviousComparableMonth.EndUtc - window.PreviousComparableMonth.StartUtc <
            window.NowUtc - window.MonthToDate.StartUtc);
    }

    /// <summary>
    /// Earlier on that same day the clamp does not apply, and the baseline is measured from the
    /// comparable day's own AEST (+10) midnight rather than by subtracting a month from the current
    /// UTC instant - which lands an hour earlier by Sydney wall clock, because the two days are in
    /// different daylight-saving offsets.
    /// </summary>
    [Fact]
    public void Resolve_MeasuresThePreviousComparableMonthFromTheComparableDaysOwnMidnight()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 11, 3, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 11, 4), window.BusinessToday);
        Assert.Equal(Utc(2026, 9, 30, 14, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2026, 10, 4, 0, 0), window.PreviousComparableMonth.EndUtc);
        Assert.NotEqual(window.NowUtc.AddMonths(-1), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(
            window.NowUtc - window.Today.StartUtc,
            window.PreviousComparableMonth.EndUtc - Utc(2026, 10, 3, 14, 0));
    }

    /// <summary>
    /// A previous month containing a daylight-saving change with the comparable day after it: the
    /// month starts at an AEST (+10) midnight and the comparable day at an AEDT (+11) one, each
    /// resolved through the business calendar rather than from one offset for the whole month.
    /// </summary>
    [Fact]
    public void Resolve_ResolvesTheMonthStartAndTheComparableDayInTheirOwnOffsets()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 11, 19, 23, 0, 0, DateTimeKind.Utc));

        var window = time.Window;

        Assert.Equal(new DateTime(2026, 11, 20), window.BusinessToday);
        Assert.Equal(Utc(2026, 9, 30, 14, 0), window.PreviousComparableMonth.StartUtc);
        Assert.Equal(Utc(2026, 10, 19, 23, 0), window.PreviousComparableMonth.EndUtc);
        Assert.Equal(new DateTime(2026, 10, 20), window.PreviousComparableMonth.LastBusinessDate);
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
        Assert.Equal(
            (new DateTime(2026, 3, 11), new DateTime(2026, 3, 11)),
            BusinessDates(window.PreviousComparableDay));
        Assert.Equal((new DateTime(2026, 3, 9), new DateTime(2026, 3, 12)), BusinessDates(window.CurrentWeek));
        Assert.Equal((new DateTime(2026, 3, 2), new DateTime(2026, 3, 5)), BusinessDates(window.PreviousComparableWeek));
        Assert.Equal((new DateTime(2026, 3, 2), new DateTime(2026, 3, 8)), BusinessDates(window.LastWeek));
        Assert.Equal((new DateTime(2026, 3, 1), new DateTime(2026, 3, 12)), BusinessDates(window.MonthToDate));
        Assert.Equal(
            (new DateTime(2026, 2, 1), new DateTime(2026, 2, 12)),
            BusinessDates(window.PreviousComparableMonth));
        Assert.Equal((new DateTime(2026, 2, 23), new DateTime(2026, 3, 1)), BusinessDates(window.TwoWeeksAgo));
        Assert.Equal((new DateTime(2026, 3, 11), new DateTime(2026, 3, 11)), BusinessDates(window.PreviousDay));

        // Each period's own instants fall on the first and last business date it claims to cover.
        MachineDashboardPeriodUtc[] periods =
        [
            window.Today, window.PreviousComparableDay, window.CurrentWeek, window.PreviousComparableWeek,
            window.LastWeek, window.MonthToDate, window.PreviousComparableMonth, window.TwoWeeksAgo,
            window.PreviousDay,
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
