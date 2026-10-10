namespace Inventory.Application.Time;

/// <summary>
/// Narrow port for converting UTC instants to the current business's calendar date and back.
/// Every authoritative business-reporting date decision goes through this port rather than
/// server-local or browser-local time, because the API host's local timezone is not the
/// business's.
///
/// The calendar is the <em>current business's</em> (issue #499): it is resolved per request from
/// the time zone configured on the business that tenancy resolution produced from the
/// authenticated actor's membership, never from a route, query, body or header value, and never
/// from a fixed zone. A request with no resolved business, or a business whose stored zone the
/// host cannot resolve, has no calendar at all and every member of this port fails closed with
/// <see cref="BusinessTimeZoneUnavailableException"/> rather than dating its financial history in
/// a calendar that is not its own.
/// </summary>
public interface IBusinessCalendar
{
    /// <summary>The current business date in its own time zone, derived from the current instant.</summary>
    DateTime Today { get; }

    /// <summary>The business calendar date that a UTC instant falls on.</summary>
    DateTime ToBusinessDate(DateTime utcInstant);

    /// <summary>
    /// The UTC instant of midnight at the start of a business date in the business's own time
    /// zone. Combined with <c>StartOfBusinessDayUtc(date.AddDays(1))</c> as an exclusive upper
    /// bound, this produces the inclusive-date-range UTC boundaries for a business date, correctly
    /// accounting for daylight saving.
    /// </summary>
    DateTime StartOfBusinessDayUtc(DateTime businessDate);
}
