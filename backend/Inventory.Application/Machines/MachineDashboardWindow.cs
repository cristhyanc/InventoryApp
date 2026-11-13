using Inventory.Application.Time;
using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// One dashboard comparison period, in both time bases it is measured in: the pair of UTC instants its
/// sales are selected between, inclusive at both ends exactly as the dashboard has always compared
/// them, and the first and last business date those instants cover. The two
/// describe the same period: the instants are the business dates' own business-day midnight boundaries.
///
/// Both are needed because the dashboard's financial inputs are measured in both bases. Sales are
/// persisted UTC instants, so revenue and commission are selected by instant. The Nayax processing fee
/// engine is a date-range contract - imported fee data is authoritative per day it covers, and an
/// estimated fee uses the rate effective on the transaction's day - so fees are charged by business
/// date. Truncating the instants to UTC dates instead would charge a business-day period for the fees of
/// every UTC day it touches, which for a business day that straddles two UTC dates subtracts fees for
/// sales the period's own revenue excludes (issue #310).
/// </summary>
public readonly record struct MachineDashboardPeriodUtc(
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime FirstBusinessDate,
    DateTime LastBusinessDate);

/// <summary>
/// The machine and site dashboards' reference window (issue #310): the current instant, the
/// business date it falls on, and the nine rolling comparison periods
/// (today, the previous comparable day, week-to-date, the previous comparable week, last full week,
/// month-to-date, the previous comparable month, two weeks ago, and the whole previous day)
/// expressed as UTC instants.
///
/// <para><see cref="PreviousComparableDay"/> is Today's own baseline (issue #1177): yesterday's
/// business day up to the same elapsed time since business-local midnight as now is into today's,
/// which is the comparison <see cref="PreviousComparableWeek"/> already makes for the week. It is
/// derived here from <see cref="IBusinessCalendar.StartOfBusinessDayUtc"/> and the business date,
/// with no new calendar member and no period arithmetic of its own.</para>
///
/// <para><see cref="PreviousComparableMonth"/> is Month to date's own baseline (issue #1180), built
/// by the same rule one month back: the previous calendar month from its own first business day's
/// midnight to the same point into that month's comparable day - today's day of the month, or that
/// month's last day when it has fewer days - as now is into today. It too adds no
/// <see cref="IBusinessCalendar"/> member.</para>
///
/// <para><see cref="PreviousDay"/> is yesterday in full (issue #1215): the previous business date
/// from its own business-local midnight to one <em>tick</em> before today's business-local
/// midnight - the last instant a <see cref="DateTime"/> can hold before it - so with the inclusive
/// filters every dashboard read uses it is exactly the half-open day [yesterday's midnight, today's
/// midnight): it ends where <see cref="Today"/> begins, includes every sale made later yesterday
/// than the current time today, and also includes a sale stored inside yesterday's final
/// millisecond, which a millisecond-earlier end would drop from both days. It is the Dashboard's
/// Today baseline since issue #1215.
/// <see cref="PreviousComparableDay"/> keeps its same-elapsed-time meaning, unchanged, for every
/// existing caller. Across a daylight-saving transition the whole previous day is 23 or 25 hours
/// long, because both of its boundaries come from <see cref="IBusinessCalendar.StartOfBusinessDayUtc"/>
/// rather than from a fixed 24 hours.</para>
///
/// The day boundaries come from the business day through <see cref="IBusinessCalendar"/>,
/// never from the host's local timezone - an App Service host runs in UTC, where a server-local
/// "today" starts up to eleven hours after the business day it is supposed to mean. The boundaries
/// are then converted back to UTC instants because the sales facts the periods select
/// (<c>NayaxSales.MachineAuthorizationTime</c>) are persisted true UTC instants, so period and sale
/// must be compared in the same time base. Daylight saving is handled by
/// <see cref="IBusinessCalendar.StartOfBusinessDayUtc"/> rather than a fixed offset, so an AEST
/// week and an AEDT week each start at their own correct instant.
///
/// The range arithmetic itself stays in <see cref="MachineDashboardPeriods"/>, unchanged: this type
/// only decides which reference date feeds it and which time base its boundaries are returned in.
/// </summary>
public sealed record MachineDashboardWindow(
    DateTime NowUtc,
    DateTime BusinessToday,
    MachineDashboardPeriodUtc Today,
    MachineDashboardPeriodUtc PreviousComparableDay,
    MachineDashboardPeriodUtc CurrentWeek,
    MachineDashboardPeriodUtc PreviousComparableWeek,
    MachineDashboardPeriodUtc LastWeek,
    MachineDashboardPeriodUtc MonthToDate,
    MachineDashboardPeriodUtc TwoWeeksAgo,
    MachineDashboardPeriodUtc PreviousComparableMonth,
    MachineDashboardPeriodUtc PreviousDay)
{
    /// <summary>
    /// Resolves the window from one current instant. The business date is derived from that same
    /// instant rather than read separately, so a request that crosses a business-day midnight cannot mix
    /// one business day's boundaries with another instant's "now".
    /// </summary>
    public static MachineDashboardWindow Resolve(IClock clock, IBusinessCalendar businessCalendar)
    {
        var nowUtc = clock.UtcNow;
        var businessToday = businessCalendar.ToBusinessDate(nowUtc);

        var currentWeek = MachineDashboardPeriods.WeekRange(businessToday);
        var previousComparableWeek = MachineDashboardPeriods.PreviousComparableWeek(businessToday);
        var lastWeek = MachineDashboardPeriods.WeekRange(businessToday, -1);
        var twoWeeksAgo = MachineDashboardPeriods.WeekRange(businessToday, -2);
        var monthToDate = MachineDashboardPeriods.MonthToDate(businessToday);

        var currentWeekStartUtc = businessCalendar.StartOfBusinessDayUtc(currentWeek.Start);
        var previousComparableWeekStartUtc = businessCalendar.StartOfBusinessDayUtc(previousComparableWeek.Start);

        var todayStartUtc = businessCalendar.StartOfBusinessDayUtc(businessToday);
        var monthToDateStartUtc = businessCalendar.StartOfBusinessDayUtc(monthToDate.Start);
        var previousBusinessDate = businessToday.AddDays(-1);
        var previousComparableDayStartUtc = businessCalendar.StartOfBusinessDayUtc(previousBusinessDate);

        // Month to date's own baseline (issue #1180). The previous calendar month is derived from the
        // business date rather than from the current instant, so a request taken in the hours where
        // the business date and the UTC date differ - and a January read, whose previous month is in
        // the previous year - names the same month the published month-to-date figure does.
        var previousMonthStart = monthToDate.Start.AddMonths(-1);
        var previousMonthLength = DateTime.DaysInMonth(previousMonthStart.Year, previousMonthStart.Month);

        // "The same point last month" is today's day of the month, held at the previous month's last
        // day when that month is shorter: there is no 31 February to compare a 31 March with.
        var comparableDate = previousMonthStart.AddDays(Math.Min(businessToday.Day, previousMonthLength) - 1);
        var previousComparableMonthStartUtc = businessCalendar.StartOfBusinessDayUtc(previousMonthStart);

        return new MachineDashboardWindow(
            nowUtc,
            businessToday,
            Today: new(todayStartUtc, nowUtc, businessToday, businessToday),
            PreviousComparableDay: new(
                previousComparableDayStartUtc,
                ComparableEndUtc(previousComparableDayStartUtc, todayStartUtc, nowUtc),
                previousBusinessDate,
                previousBusinessDate),
            CurrentWeek: new(currentWeekStartUtc, nowUtc, currentWeek.Start, businessToday),
            PreviousComparableWeek: new(
                previousComparableWeekStartUtc,
                ComparableEndUtc(previousComparableWeekStartUtc, currentWeekStartUtc, nowUtc),
                previousComparableWeek.Start,
                previousComparableWeek.End.Date),
            LastWeek: new(
                businessCalendar.StartOfBusinessDayUtc(lastWeek.Start),
                EndOfBusinessDayUtc(businessCalendar, lastWeek.End),
                lastWeek.Start,
                lastWeek.End.Date),
            MonthToDate: new(monthToDateStartUtc, nowUtc, monthToDate.Start, businessToday),
            TwoWeeksAgo: new(
                businessCalendar.StartOfBusinessDayUtc(twoWeeksAgo.Start),
                EndOfBusinessDayUtc(businessCalendar, twoWeeksAgo.End),
                twoWeeksAgo.Start,
                twoWeeksAgo.End.Date),
            PreviousComparableMonth: new(
                previousComparableMonthStartUtc,
                businessToday.Day > previousMonthLength
                    // The comparable day is the shorter month's last day and today is past it, so the
                    // baseline is that month in full: its own last instant, which is one millisecond
                    // before this month began.
                    ? monthToDateStartUtc.AddMilliseconds(-1)
                    : ComparableEndUtc(
                        businessCalendar.StartOfBusinessDayUtc(comparableDate),
                        todayStartUtc,
                        nowUtc,
                        EndOfBusinessDayUtc(businessCalendar, comparableDate)),
                previousMonthStart,
                comparableDate),
            // Yesterday in full (issue #1215): from its own midnight to the last tick before
            // today's, whatever the time is now. A tick rather than a millisecond, so the inclusive
            // filters make this the half-open day and a sale inside its final millisecond is not
            // lost between yesterday and today.
            PreviousDay: new(
                previousComparableDayStartUtc,
                todayStartUtc.AddTicks(-1),
                previousBusinessDate,
                previousBusinessDate));
    }

    /// <summary>
    /// A comparison baseline's inclusive end: the same elapsed trading time into the previous period
    /// as now is into the current one, which is what makes the two comparable. One rule serves both
    /// baselines - the previous comparable week
    /// (<see cref="MachineDashboardPeriods.PreviousComparableWeek"/> defines it as the current
    /// week-to-date shifted back one week) and, since issue #1177, the previous comparable day - so
    /// "the same time last week" and "the same time yesterday" cannot come to mean two different
    /// things.
    ///
    /// Both endpoints are measured from their own period's business-day midnight, never by
    /// subtracting seven days or twenty-four hours from the current UTC instant: across a
    /// daylight-saving transition the two periods start an hour apart in UTC, so on the day after the
    /// transition that subtraction lands before the previous period even began and the comparison
    /// period is empty.
    ///
    /// Held at the previous period's own end when the current one is the longer of the two - the week
    /// daylight saving ends is 169 hours and the day it ends is 25 - so the baseline can never reach
    /// into the current period and count the same sale in both.
    ///
    /// <para><paramref name="previousPeriodEndUtc"/> is that holding point. For the day and the week
    /// it is the instant before the current period begins, which is also the previous period's own
    /// last instant, so it is left unstated. The previous comparable month states it (issue #1180):
    /// the elapsed time is measured into one <em>day</em> of that month - the comparable day - so the
    /// baseline is held at the end of that day rather than at the end of the month, which may be
    /// weeks later.</para>
    /// </summary>
    private static DateTime ComparableEndUtc(
        DateTime previousPeriodStartUtc,
        DateTime currentPeriodStartUtc,
        DateTime nowUtc,
        DateTime? previousPeriodEndUtc = null)
    {
        var endUtc = previousPeriodStartUtc + (nowUtc - currentPeriodStartUtc);
        var heldAtUtc = previousPeriodEndUtc ?? currentPeriodStartUtc.AddMilliseconds(-1);
        return endUtc <= heldAtUtc ? endUtc : heldAtUtc;
    }

    /// <summary>
    /// The UTC instant of a completed business week's inclusive end, which
    /// <see cref="MachineDashboardPeriods.WeekRange"/> expresses as one millisecond before the next
    /// business day begins. Derived from the following day's start rather than by adding a fixed
    /// number of hours, so a week containing a daylight-saving transition still ends exactly when
    /// the next business day starts.
    /// </summary>
    private static DateTime EndOfBusinessDayUtc(IBusinessCalendar businessCalendar, DateTime businessDayEnd) =>
        businessCalendar.StartOfBusinessDayUtc(businessDayEnd.Date.AddDays(1)).AddMilliseconds(-1);
}
