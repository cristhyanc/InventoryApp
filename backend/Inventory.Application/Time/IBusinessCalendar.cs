namespace Inventory.Application.Time;

/// <summary>
/// Narrow port for converting UTC instants to the <c>Australia/Sydney</c> business calendar date and back.
/// Every authoritative business-reporting date decision goes through this port rather than server-local
/// or browser-local time, because the API host's local timezone is not the business's.
/// </summary>
public interface IBusinessCalendar
{
    /// <summary>The current business date in <c>Australia/Sydney</c>, derived from the current instant.</summary>
    DateTime Today { get; }

    /// <summary>The <c>Australia/Sydney</c> calendar date that a UTC instant falls on.</summary>
    DateTime ToBusinessDate(DateTime utcInstant);

    /// <summary>
    /// The UTC instant of midnight at the start of a business date in <c>Australia/Sydney</c>. Combined
    /// with <c>StartOfBusinessDayUtc(date.AddDays(1))</c> as an exclusive upper bound, this produces the
    /// inclusive-date-range UTC boundaries for a business date, correctly accounting for daylight saving.
    /// </summary>
    DateTime StartOfBusinessDayUtc(DateTime businessDate);
}
