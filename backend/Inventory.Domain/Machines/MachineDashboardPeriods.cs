namespace Inventory.Domain.Machines;

/// <summary>
/// The machine/site dashboard's rolling comparison periods (today, week-to-date, the previous
/// comparable week, last full week, month-to-date, two weeks ago), given an already-resolved
/// reference instant. Mirrors the former <c>InventoryApi.Services.MachineService</c>
/// <c>GetWeekRange</c>/<c>GetWeekToDateRange</c>/<c>GetPreviousComparableWeekRange</c>/
/// <c>GetMonthToDateRange</c> static helpers exactly (issue #241). The reference instant is still
/// acquired with the server-local clock at the call site, unchanged from before this migration and
/// tracked as an existing follow-up (see docs/architecture.md § Time); this type only makes the
/// range arithmetic itself deterministic and testable in isolation.
/// </summary>
public static class MachineDashboardPeriods
{
    public static (DateTime Start, DateTime End) WeekRange(DateTime referenceDate, int weeksOffset = 0)
    {
        var date = referenceDate.Date.AddDays(weeksOffset * 7);
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        var startOfWeek = date.AddDays(-diff);
        var endOfWeek = startOfWeek.AddDays(7).AddMilliseconds(-1);
        return (startOfWeek, endOfWeek);
    }

    public static (DateTime Start, DateTime End) WeekToDate(DateTime referenceDate)
    {
        var start = WeekRange(referenceDate.Date).Start;
        return (start, referenceDate);
    }

    public static (DateTime Start, DateTime End) PreviousComparableWeek(DateTime referenceDate)
    {
        var current = WeekToDate(referenceDate);
        return (current.Start.AddDays(-7), current.End.AddDays(-7));
    }

    public static (DateTime Start, DateTime End) MonthToDate(DateTime referenceDate) =>
        (new DateTime(referenceDate.Year, referenceDate.Month, 1), referenceDate);
}
