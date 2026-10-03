using Inventory.Application.Machines;
using Inventory.Application.Time;
using Inventory.Infrastructure.Time;

namespace InventoryApi.Tests.Application.Time;

/// <summary>
/// A fixed UTC instant paired with the production <see cref="SydneyBusinessCalendar"/> over it, for
/// the tests that must exercise real <c>Australia/Sydney</c> behaviour - a UTC instant falling on a
/// different Sydney date, and the AEST/AEDT daylight-saving transitions - rather than
/// <see cref="FakeBusinessCalendar"/>'s deliberately trivial identity conversion.
/// </summary>
public sealed class FixedSydneyTime
{
    public FixedSydneyTime(DateTime nowUtc)
    {
        Clock = new FakeClock(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        Calendar = new SydneyBusinessCalendar(Clock);
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
