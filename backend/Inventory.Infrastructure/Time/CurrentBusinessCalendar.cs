using Inventory.Application.Time;

namespace Inventory.Infrastructure.Time;

/// <summary>
/// The per-request <see cref="IBusinessCalendar"/> (issue #499): the business calendar of the time
/// zone configured on the business this request resolved, and of no other zone.
///
/// It reads that zone from <see cref="IBusinessTimeZoneProvider"/>, which the API boundary
/// publishes once per request from the trusted current business, so the calendar never touches
/// request input and never queries anything itself. The resolution is memoised for the lifetime of
/// the request scope: several use cases in one request cannot disagree about which calendar they
/// are in, and the platform time-zone lookup happens at most once.
///
/// It fails closed. A request with no resolved business, or a business whose stored id the host's
/// time-zone database cannot resolve, gets <see cref="BusinessTimeZoneUnavailableException"/> from
/// every member rather than <c>Australia/Sydney</c>, the host's own zone or UTC: an unnoticed
/// substitution would move sales, fees, commissions and profit between business days.
/// </summary>
public sealed class CurrentBusinessCalendar : IBusinessCalendar
{
    private readonly IClock _clock;
    private readonly IBusinessTimeZoneProvider _timeZoneProvider;
    private ZonedBusinessCalendar? _calendar;

    public CurrentBusinessCalendar(IClock clock, IBusinessTimeZoneProvider timeZoneProvider)
    {
        _clock = clock;
        _timeZoneProvider = timeZoneProvider;
    }

    public DateTime Today => Calendar().Today;

    public DateTime ToBusinessDate(DateTime utcInstant) => Calendar().ToBusinessDate(utcInstant);

    public DateTime StartOfBusinessDayUtc(DateTime businessDate) => Calendar().StartOfBusinessDayUtc(businessDate);

    private ZonedBusinessCalendar Calendar()
    {
        if (_calendar is not null)
        {
            return _calendar;
        }

        var timeZoneId = _timeZoneProvider.TimeZoneId;
        if (!BusinessTimeZones.TryResolve(timeZoneId, out var timeZone))
        {
            throw new BusinessTimeZoneUnavailableException(timeZoneId);
        }

        return _calendar = new ZonedBusinessCalendar(_clock, timeZone);
    }
}
