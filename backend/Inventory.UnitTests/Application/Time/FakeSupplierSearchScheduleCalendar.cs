using Inventory.Application.Time;

namespace InventoryApi.Tests.Application.Time;

/// <summary>
/// A stand-in for the platform collection calendar (issue #1108): a settable date, and a fixed
/// offset from UTC for the start of one of its days.
///
/// <para>The offset is fixed on purpose. What a test of the due rule or of the work list needs is a
/// known occurrence start, and a double that resolved the real zone would make every such test
/// depend on the host's time-zone database. The real conversion - including the two days a year the
/// offset changes - is <c>Inventory.Infrastructure.Time.SupplierSearchScheduleCalendar</c>'s and is
/// covered against the platform database in <c>SupplierSearchScheduleCalendarTests</c>.</para>
/// </summary>
public sealed class FakeSupplierSearchScheduleCalendar : ISupplierSearchScheduleCalendar
{
    private readonly TimeSpan _offsetFromUtc;

    /// <param name="today">The date the calendar reports as today.</param>
    /// <param name="offsetFromUtc">
    /// How far ahead of UTC the calendar is, so <see cref="StartOfDayUtc"/> answers a real instant.
    /// Defaults to eleven hours, which is what <c>Australia/Sydney</c> is on a summer date.
    /// </param>
    public FakeSupplierSearchScheduleCalendar(DateOnly today, TimeSpan? offsetFromUtc = null)
    {
        Today = today;
        _offsetFromUtc = offsetFromUtc ?? TimeSpan.FromHours(11);
    }

    /// <inheritdoc />
    public DateOnly Today { get; set; }

    /// <inheritdoc />
    public DateTime StartOfDayUtc(DateOnly scheduleDate) =>
        DateTime.SpecifyKind(
            scheduleDate.ToDateTime(TimeOnly.MinValue) - _offsetFromUtc,
            DateTimeKind.Utc);
}
