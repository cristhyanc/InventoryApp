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
/// business date it falls on, and the seven rolling comparison periods
/// (today, the previous comparable day, week-to-date, the previous comparable week, last full week,
/// month-to-date, two weeks ago) expressed as UTC instants.
///
/// <para><see cref="PreviousComparableDay"/> is Today's own baseline (issue #1177): yesterday's
/// business day up to the same elapsed time since business-local midnight as now is into today's,
/// which is the comparison <see cref="PreviousComparableWeek"/> already makes for the week. It is
/// derived here from <see cref="IBusinessCalendar.StartOfBusinessDayUtc"/> and the business date,
/// with no new calendar member and no period arithmetic of its own.</para>
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
    MachineDashboardPeriodUtc TwoWeeksAgo)
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
        var previousBusinessDate = businessToday.AddDays(-1);
        var previousComparableDayStartUtc = businessCalendar.StartOfBusinessDayUtc(previousBusinessDate);

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
            MonthToDate: new(
                businessCalendar.StartOfBusinessDayUtc(monthToDate.Start), nowUtc, monthToDate.Start, businessToday),
            TwoWeeksAgo: new(
                businessCalendar.StartOfBusinessDayUtc(twoWeeksAgo.Start),
                EndOfBusinessDayUtc(businessCalendar, twoWeeksAgo.End),
                twoWeeksAgo.Start,
                twoWeeksAgo.End.Date));
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
    /// </summary>
    private static DateTime ComparableEndUtc(
        DateTime previousPeriodStartUtc, DateTime currentPeriodStartUtc, DateTime nowUtc)
    {
        var endUtc = previousPeriodStartUtc + (nowUtc - currentPeriodStartUtc);
        var previousPeriodEndUtc = currentPeriodStartUtc.AddMilliseconds(-1);
        return endUtc <= previousPeriodEndUtc ? endUtc : previousPeriodEndUtc;
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
