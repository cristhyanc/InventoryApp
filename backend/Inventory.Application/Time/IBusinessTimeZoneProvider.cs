namespace Inventory.Application.Time;

/// <summary>
/// The current business's time zone as <see cref="IBusinessCalendar"/> needs it (issue #499):
/// synchronously, and without an await.
///
/// It is the time-zone counterpart of <c>IBusinessScope</c> and exists for the same reason.
/// Resolving the current business is asynchronous because it reads membership and business rows,
/// while converting an instant to a business date is a synchronous conversion that happens deep
/// inside a calculation. The resolved zone is therefore published once per request, by the API
/// boundary that already resolves the business, and read from here afterwards.
///
/// <see langword="null"/> means "not resolved", never "use a default": a consumer must fail closed
/// rather than fall back to <c>Australia/Sydney</c> or to the host's own zone, because both would
/// silently date a business's financial history in a calendar that is not its own.
/// </summary>
public interface IBusinessTimeZoneProvider
{
    /// <summary>
    /// The IANA time-zone id of the business resolved for this request, or <see langword="null"/>
    /// when no business has been resolved.
    /// </summary>
    string? TimeZoneId { get; }
}
