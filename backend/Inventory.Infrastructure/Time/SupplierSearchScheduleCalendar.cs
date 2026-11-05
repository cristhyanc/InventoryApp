using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Time;

/// <summary>
/// The platform collection calendar of issue #1108, over the one zone
/// <see cref="SupplierSearchSchedulePolicy.TimeZoneId"/> declares.
///
/// <para><strong>It is the same conversion every business date uses.</strong> It delegates to
/// <see cref="ZonedBusinessCalendar"/>, which goes through the platform time-zone database and
/// already answers the two days a year a plain conversion gets wrong - a midnight that happens twice
/// and a midnight that never happens. Hand-rolling a second conversion here would be a second answer
/// to what a day starts at, which is exactly what <see cref="BusinessTimeZones"/> exists to prevent;
/// what differs is only <em>which</em> zone, and that is the one thing this type decides.</para>
///
/// <para><strong>The zone is resolved on first use, not on construction.</strong> A host whose
/// time-zone database cannot resolve the id fails closed - the same
/// <see cref="TimeZoneNotFoundException"/> <see cref="BusinessTimeZones.Resolve"/> raises - and
/// resolving lazily keeps that failure on the one request that actually needs a collection date
/// rather than on every request that happens to construct a scope.</para>
/// </summary>
public sealed class SupplierSearchScheduleCalendar : ISupplierSearchScheduleCalendar
{
    private readonly IClock _clock;
    private ZonedBusinessCalendar? _calendar;

    public SupplierSearchScheduleCalendar(IClock clock)
    {
        _clock = clock;
    }

    /// <inheritdoc />
    public DateOnly Today => DateOnly.FromDateTime(Calendar.Today);

    /// <inheritdoc />
    public DateTime StartOfDayUtc(DateOnly scheduleDate) =>
        Calendar.StartOfBusinessDayUtc(scheduleDate.ToDateTime(TimeOnly.MinValue));

    /// <exception cref="TimeZoneNotFoundException">
    /// The host cannot resolve the collection zone, so no collection date can be derived at all. It
    /// is a refusal rather than a substitution for the reason a business's own missing zone is one.
    /// </exception>
    private ZonedBusinessCalendar Calendar =>
        _calendar ??= ZonedBusinessCalendar.ForTimeZone(
            _clock,
            SupplierSearchSchedulePolicy.TimeZoneId);
}
