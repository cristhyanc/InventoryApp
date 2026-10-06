using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

public class MachineDashboardPeriodsTests
{
    [Fact]
    public void Week_to_date_comparison_uses_same_elapsed_period()
    {
        var reference = new DateTime(2026, 9, 4, 14, 30, 0);

        var current = MachineDashboardPeriods.WeekToDate(reference);
        var previous = MachineDashboardPeriods.PreviousComparableWeek(reference);

        Assert.Equal(new DateTime(2026, 8, 31), current.Start);
        Assert.Equal(reference, current.End);
        Assert.Equal(new DateTime(2026, 8, 24), previous.Start);
        Assert.Equal(new DateTime(2026, 8, 28, 14, 30, 0), previous.End);
    }

    [Fact]
    public void Month_to_date_starts_at_the_first_local_day()
    {
        var range = MachineDashboardPeriods.MonthToDate(new DateTime(2026, 9, 4, 14, 30, 0));

        Assert.Equal(new DateTime(2026, 9, 1), range.Start);
        Assert.Equal(new DateTime(2026, 9, 4, 14, 30, 0), range.End);
    }

    [Fact]
    public void Week_range_starts_on_monday_and_ends_just_before_the_next_monday()
    {
        var range = MachineDashboardPeriods.WeekRange(new DateTime(2026, 9, 4));

        Assert.Equal(new DateTime(2026, 8, 31), range.Start);
        Assert.Equal(new DateTime(2026, 9, 6, 23, 59, 59, 999), range.End);
    }

    [Fact]
    public void Week_range_offset_shifts_by_whole_weeks()
    {
        var range = MachineDashboardPeriods.WeekRange(new DateTime(2026, 9, 4), -2);

        Assert.Equal(new DateTime(2026, 8, 17), range.Start);
    }
}
