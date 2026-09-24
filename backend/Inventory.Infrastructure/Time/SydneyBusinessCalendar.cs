using Inventory.Application.Time;

namespace Inventory.Infrastructure.Time;

/// <summary>
/// Converts UTC instants to <c>Australia/Sydney</c> business calendar dates using the IANA/Windows
/// timezone database, so daylight-saving transitions are handled by the platform rather than a
/// hand-rolled offset table.
/// </summary>
public sealed class SydneyBusinessCalendar : IBusinessCalendar
{
    private static readonly TimeZoneInfo SydneyTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private readonly IClock _clock;

    public SydneyBusinessCalendar(IClock clock)
    {
        _clock = clock;
    }

    public DateTime Today => ToBusinessDate(_clock.UtcNow);

    public DateTime ToBusinessDate(DateTime utcInstant)
    {
        var utc = DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SydneyTimeZone).Date;
    }

    public DateTime StartOfBusinessDayUtc(DateTime businessDate)
    {
        var sydneyMidnight = DateTime.SpecifyKind(businessDate.Date, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(sydneyMidnight, SydneyTimeZone);
    }
}
