using Inventory.Application.Time;

namespace Inventory.Infrastructure.Time;

/// <summary>
/// Converts UTC instants to business calendar dates in one explicit time zone, using the platform
/// time-zone database so daylight-saving transitions are handled by the platform rather than a
/// hand-rolled offset table.
///
/// It knows nothing about tenancy: it is handed the zone it works in. <see cref="CurrentBusinessCalendar"/>
/// is what resolves that zone per request from the trusted current business (issue #499), and a
/// test or a human-invoked maintenance command that legitimately knows its own zone constructs
/// this type directly with it.
/// </summary>
public sealed class ZonedBusinessCalendar : IBusinessCalendar
{
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;

    public ZonedBusinessCalendar(IClock clock, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        _clock = clock;
        _timeZone = timeZone;
    }

    /// <summary>
    /// The calendar for an IANA time-zone id, resolved through <see cref="BusinessTimeZones"/>.
    /// </summary>
    /// <exception cref="TimeZoneNotFoundException">The id is not one this host can resolve.</exception>
    public static ZonedBusinessCalendar ForTimeZone(IClock clock, string ianaTimeZoneId) =>
        new(clock, BusinessTimeZones.Resolve(ianaTimeZoneId));

    /// <summary>The IANA id this calendar derives its dates in, for diagnostics and tests.</summary>
    public string TimeZoneId => _timeZone.Id;

    public DateTime Today => ToBusinessDate(_clock.UtcNow);

    public DateTime ToBusinessDate(DateTime utcInstant)
    {
        var utc = DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, _timeZone).Date;
    }

    public DateTime StartOfBusinessDayUtc(DateTime businessDate)
    {
        var localMidnight = DateTime.SpecifyKind(businessDate.Date, DateTimeKind.Unspecified);

        // Most zones move their clocks in the small hours, so midnight exists exactly once and
        // the conversion is unambiguous. Some real IANA zones transition at or across midnight
        // (Africa/Cairo and Asia/Tehran start daylight saving at 00:00, America/Havana ends it at
        // 01:00), and for those the start of the business day is still a single real instant - it
        // is just not the instant a plain conversion would give. These two cases are what keep a
        // business in such a zone from losing, or double-counting, the first hour of one day a
        // year; the ordinary path below is unchanged for every other day and every other zone.
        if (_timeZone.IsAmbiguousTime(localMidnight))
        {
            // Midnight happens twice: the day starts at the first of the two instants, which is
            // the one with the larger offset from UTC.
            var earliestOffset = _timeZone.GetAmbiguousTimeOffsets(localMidnight).Max();
            return DateTime.SpecifyKind(localMidnight - earliestOffset, DateTimeKind.Utc);
        }

        if (_timeZone.IsInvalidTime(localMidnight))
        {
            // Midnight never happens: the day starts the moment the clocks jump past it.
            return FirstValidInstantAfter(localMidnight);
        }

        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, _timeZone);
    }

    /// <summary>
    /// The UTC instant of the first local wall-clock time after a skipped <paramref name="localMidnight"/>
    /// that the zone actually shows, which is the transition instant itself. Stepped a minute at a
    /// time because a skipped interval's length is a property of the zone's own rules, not a fixed
    /// hour, and bounded by the day so a pathological rule cannot loop.
    /// </summary>
    private DateTime FirstValidInstantAfter(DateTime localMidnight)
    {
        for (var minutes = 1; minutes <= 24 * 60; minutes++)
        {
            var candidate = localMidnight.AddMinutes(minutes);
            if (!_timeZone.IsInvalidTime(candidate))
            {
                return TimeZoneInfo.ConvertTimeToUtc(candidate, _timeZone);
            }
        }

        throw new InvalidTimeZoneException(
            $"Time zone '{_timeZone.Id}' shows no wall-clock time at all on {localMidnight:yyyy-MM-dd}, "
                + "so the start of that business day cannot be resolved.");
    }
}
