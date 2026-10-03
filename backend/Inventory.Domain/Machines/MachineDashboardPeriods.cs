namespace Inventory.Domain.Machines;

/// <summary>
/// The machine/site dashboard's rolling comparison periods (today, week-to-date, the previous
/// comparable week, last full week, month-to-date, two weeks ago), given an already-resolved
/// reference instant. Mirrors the former <c>InventoryApi.Services.MachineService</c>
/// <c>GetWeekRange</c>/<c>GetWeekToDateRange</c>/<c>GetPreviousComparableWeekRange</c>/
/// <c>GetMonthToDateRange</c> static helpers exactly (issue #241). This type only makes the range
/// arithmetic itself deterministic and testable in isolation: the reference date is resolved by the
/// caller, and since issue #310 that caller is
/// <c>Inventory.Application.Machines.MachineDashboardWindow</c>, which derives it from the
/// <c>Australia/Sydney</c> business day through the Application time ports and converts these
/// business-date boundaries back to UTC instants (see docs/architecture.md § Time).
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
