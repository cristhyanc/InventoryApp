namespace Inventory.Infrastructure.Time;

/// <summary>
/// Resolves a business's stored IANA time-zone identifier against the host's time-zone database
/// (issue #499). It is the one place the application turns a persisted id such as
/// <c>Australia/Sydney</c> or <c>America/New_York</c> into a <see cref="TimeZoneInfo"/>, so the
/// write path that validates an id and the read path that derives business dates from it can never
/// disagree about which ids are usable.
///
/// IANA ids are preferred, and when a host cannot resolve them (notably some Windows setups) the
/// id is converted with <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId(string, out string?)"/>
/// and the corresponding Windows id is resolved instead - the fallback
/// <c>SydneyBusinessCalendar</c> introduced for <c>Australia/Sydney</c>, kept for every zone. Both
/// paths use the platform database, so daylight-saving transitions are the platform's answer
/// rather than a hand-rolled offset table.
///
/// A Windows id is deliberately *not* accepted as a stored value: the stored contract is an IANA
/// id, so the same database behaves identically on a Linux and a Windows host.
/// </summary>
public static class BusinessTimeZones
{
    /// <summary>
    /// Resolves <paramref name="ianaTimeZoneId"/>, returning <see langword="false"/> for a null,
    /// blank, unknown or non-IANA id rather than throwing or substituting another zone.
    /// </summary>
    public static bool TryResolve(string? ianaTimeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;

        if (string.IsNullOrWhiteSpace(ianaTimeZoneId))
        {
            return false;
        }

        // A Windows id would resolve on a Windows host and fail on a Linux one, so it is rejected
        // on both: TryConvertWindowsIdToIanaId recognises exactly the ids that are not IANA ones.
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(ianaTimeZoneId, out _))
        {
            return false;
        }

        if (TryFindSystemTimeZone(ianaTimeZoneId, out timeZone))
        {
            return true;
        }

        return TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out var windowsId)
            && !string.IsNullOrWhiteSpace(windowsId)
            && TryFindSystemTimeZone(windowsId, out timeZone);
    }

    /// <summary>
    /// Resolves <paramref name="ianaTimeZoneId"/> or throws. For the call sites that have already
    /// established the id is a valid one and would have nothing to fall back to.
    /// </summary>
    /// <exception cref="TimeZoneNotFoundException">The id is not a resolvable IANA id.</exception>
    public static TimeZoneInfo Resolve(string? ianaTimeZoneId) =>
        TryResolve(ianaTimeZoneId, out var timeZone)
            ? timeZone
            : throw new TimeZoneNotFoundException(
                $"'{ianaTimeZoneId}' is not an IANA time-zone id this host can resolve.");

    private static bool TryFindSystemTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            // A corrupt platform entry is as unusable as a missing one, and the caller's answer is
            // the same: this id cannot derive business dates.
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
