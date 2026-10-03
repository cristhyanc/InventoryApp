using Inventory.Application.Time;
using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// One dashboard comparison period, in both time bases it is measured in: the pair of UTC instants its
/// sales are selected between, inclusive at both ends exactly as the dashboard has always compared
/// them, and the first and last <c>Australia/Sydney</c> business date those instants cover. The two
/// describe the same period: the instants are the business dates' own Sydney midnight boundaries.
///
/// Both are needed because the dashboard's financial inputs are measured in both bases. Sales are
/// persisted UTC instants, so revenue and commission are selected by instant. The Nayax processing fee
/// engine is a date-range contract - imported fee data is authoritative per day it covers, and an
/// estimated fee uses the rate effective on the transaction's day - so fees are charged by business
/// date. Truncating the instants to UTC dates instead would charge a Sydney period for the fees of
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
/// <c>Australia/Sydney</c> business date it falls on, and the six rolling comparison periods
/// (today, week-to-date, the previous comparable week, last full week, month-to-date, two weeks
/// ago) expressed as UTC instants.
///
/// The day boundaries come from the Sydney business day through <see cref="IBusinessCalendar"/>,
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
    MachineDashboardPeriodUtc CurrentWeek,
    MachineDashboardPeriodUtc PreviousComparableWeek,
    MachineDashboardPeriodUtc LastWeek,
    MachineDashboardPeriodUtc MonthToDate,
    MachineDashboardPeriodUtc TwoWeeksAgo)
{
    /// <summary>
    /// Resolves the window from one current instant. The business date is derived from that same
    /// instant rather than read separately, so a request that crosses a Sydney midnight cannot mix
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

        return new MachineDashboardWindow(
            nowUtc,
            businessToday,
            Today: new(
                businessCalendar.StartOfBusinessDayUtc(businessToday), nowUtc, businessToday, businessToday),
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
    /// The previous comparable week's inclusive end: the same elapsed trading time into the previous
    /// Sydney business week as now is into the current one, which is what makes the two periods
    /// comparable (<see cref="MachineDashboardPeriods.PreviousComparableWeek"/> defines it as the
    /// current week-to-date shifted back one week). Both endpoints are measured from their own week's
    /// Sydney Monday midnight, never by subtracting seven days from the current UTC instant: across a
    /// daylight-saving transition the two weeks start an hour apart in UTC, so on the Monday after the
    /// transition that subtraction lands before the previous week even began and the comparison period
    /// is empty.
    ///
    /// Held at the previous week's own end when the current week is the longer of the two - the week
    /// daylight saving ends is 169 hours - so the comparable period can never reach into the current
    /// week and count the same sale in both.
    /// </summary>
    private static DateTime ComparableEndUtc(
        DateTime previousComparableWeekStartUtc, DateTime currentWeekStartUtc, DateTime nowUtc)
    {
        var endUtc = previousComparableWeekStartUtc + (nowUtc - currentWeekStartUtc);
        var previousWeekEndUtc = currentWeekStartUtc.AddMilliseconds(-1);
        return endUtc <= previousWeekEndUtc ? endUtc : previousWeekEndUtc;
    }

    /// <summary>
    /// The UTC instant of a completed business week's inclusive end, which
    /// <see cref="MachineDashboardPeriods.WeekRange"/> expresses as one millisecond before the next
    /// business day begins. Derived from the following day's start rather than by adding a fixed
    /// number of hours, so a week containing a daylight-saving transition still ends exactly when
    /// the next Sydney business day starts.
    /// </summary>
    private static DateTime EndOfBusinessDayUtc(IBusinessCalendar businessCalendar, DateTime businessDayEnd) =>
        businessCalendar.StartOfBusinessDayUtc(businessDayEnd.Date.AddDays(1)).AddMilliseconds(-1);
}
