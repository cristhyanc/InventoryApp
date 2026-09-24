using Inventory.Infrastructure.Time;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Time;

public class SydneyBusinessCalendarTests
{
    [Fact]
    public void ToBusinessDate_rolls_over_at_Sydney_midnight_during_standard_time()
    {
        var calendar = new SydneyBusinessCalendar(new FakeClock(DateTime.UtcNow));

        Assert.Equal(new DateTime(2026, 7, 14),
            calendar.ToBusinessDate(new DateTime(2026, 7, 14, 13, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 15),
            calendar.ToBusinessDate(new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ToBusinessDate_rolls_over_at_Sydney_midnight_during_daylight_saving()
    {
        var calendar = new SydneyBusinessCalendar(new FakeClock(DateTime.UtcNow));

        Assert.Equal(new DateTime(2026, 1, 14),
            calendar.ToBusinessDate(new DateTime(2026, 1, 14, 12, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 1, 15),
            calendar.ToBusinessDate(new DateTime(2026, 1, 14, 13, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_uses_AEST_offset_outside_daylight_saving()
    {
        var calendar = new SydneyBusinessCalendar(new FakeClock(DateTime.UtcNow));

        Assert.Equal(new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_uses_AEDT_offset_during_daylight_saving()
    {
        var calendar = new SydneyBusinessCalendar(new FakeClock(DateTime.UtcNow));

        Assert.Equal(new DateTime(2026, 1, 14, 13, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 1, 15)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_still_uses_AEST_on_the_day_daylight_saving_starts()
    {
        // 2026-10-04 is the day Sydney clocks jump forward from AEST to AEDT at 2am local;
        // midnight that day is still governed by the outgoing AEST (+10) offset.
        var calendar = new SydneyBusinessCalendar(new FakeClock(DateTime.UtcNow));

        Assert.Equal(new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 10, 4)));
    }

    [Fact]
    public void StartOfBusinessDayUtc_still_uses_AEDT_on_the_day_daylight_saving_ends()
    {
        // 2026-04-05 is the day Sydney clocks fall back from AEDT to AEST at 3am local;
        // midnight that day is still governed by the outgoing AEDT (+11) offset.
        var calendar = new SydneyBusinessCalendar(new FakeClock(DateTime.UtcNow));

        Assert.Equal(new DateTime(2026, 4, 4, 13, 0, 0, DateTimeKind.Utc),
            calendar.StartOfBusinessDayUtc(new DateTime(2026, 4, 5)));
    }

    [Fact]
    public void Today_derives_the_Sydney_business_date_from_the_clock()
    {
        var clock = new FakeClock(new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc));
        var calendar = new SydneyBusinessCalendar(clock);

        Assert.Equal(new DateTime(2026, 7, 15), calendar.Today);
    }
}
