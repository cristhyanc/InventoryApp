using Inventory.Application.Machines;
using Inventory.Application.Time;
using Inventory.Infrastructure.Time;

namespace InventoryApi.Tests.Application.Time;

/// <summary>
/// A fixed UTC instant paired with the production <see cref="ZonedBusinessCalendar"/> over
/// <c>Australia/Sydney</c>, for the tests that must exercise real Sydney behaviour - a UTC instant
/// falling on a different Sydney date, and the AEST/AEDT daylight-saving transitions - rather than
/// <see cref="FakeBusinessCalendar"/>'s deliberately trivial identity conversion.
///
/// Sydney is named here because these tests describe a business configured for Sydney, which is
/// what the application's existing business is (issue #499); the calendar type itself is
/// per-business, and <see cref="ZonedBusinessCalendar.ForTimeZone"/> builds one for any zone.
/// </summary>
public sealed class FixedSydneyTime
{
    public const string SydneyTimeZoneId = "Australia/Sydney";

    public FixedSydneyTime(DateTime nowUtc)
    {
        Clock = new FakeClock(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        Calendar = ZonedBusinessCalendar.ForTimeZone(Clock, SydneyTimeZoneId);
    }

    public IClock Clock { get; }

    public IBusinessCalendar Calendar { get; }

    public DateTime NowUtc => Clock.UtcNow;

    /// <summary>The Sydney business date this instant falls on.</summary>
    public DateTime BusinessToday => Calendar.Today;

    /// <summary>The dashboard window the use cases resolve from this instant.</summary>
    public MachineDashboardWindow Window => MachineDashboardWindow.Resolve(Clock, Calendar);

    /// <summary>
    /// Pins the current instant once, so a test can timestamp its fixtures with exactly the instant
    /// the use case under test will later read and never straddle a real midnight mid-test.
    /// </summary>
    public static FixedSydneyTime PinnedToNow() => new(DateTime.UtcNow);
}
