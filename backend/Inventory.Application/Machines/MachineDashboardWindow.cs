using Inventory.Application.Time;
using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// One dashboard comparison period as the pair of UTC instants its sales are selected between,
/// inclusive at both ends exactly as the dashboard has always compared them.
/// </summary>
public readonly record struct MachineDashboardPeriodUtc(DateTime StartUtc, DateTime EndUtc);

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

        return new MachineDashboardWindow(
            nowUtc,
            businessToday,
            Today: new(businessCalendar.StartOfBusinessDayUtc(businessToday), nowUtc),
            CurrentWeek: new(businessCalendar.StartOfBusinessDayUtc(currentWeek.Start), nowUtc),

            // The previous comparable week is the current week-to-date shifted back seven days, as
            // MachineDashboardPeriods defines it: its start is the previous business week's Monday
            // midnight in Sydney, and its end is the same elapsed distance into that week as now is
            // into this one, so the two periods cover comparable trading time.
            PreviousComparableWeek: new(
                businessCalendar.StartOfBusinessDayUtc(previousComparableWeek.Start), nowUtc.AddDays(-7)),

            LastWeek: new(
                businessCalendar.StartOfBusinessDayUtc(lastWeek.Start),
                EndOfBusinessDayUtc(businessCalendar, lastWeek.End)),
            MonthToDate: new(businessCalendar.StartOfBusinessDayUtc(monthToDate.Start), nowUtc),
            TwoWeeksAgo: new(
                businessCalendar.StartOfBusinessDayUtc(twoWeeksAgo.Start),
                EndOfBusinessDayUtc(businessCalendar, twoWeeksAgo.End)));
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
