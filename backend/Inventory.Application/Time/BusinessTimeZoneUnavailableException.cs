namespace Inventory.Application.Time;

/// <summary>
/// No business calendar could be built for this request (issue #499): either no business was
/// resolved for it, or the business's stored IANA time zone is not one the host's time-zone
/// database can resolve.
///
/// It is deliberately a hard failure rather than a fallback. A business date decided by
/// <c>Australia/Sydney</c>, by the host's own zone, or by UTC, for a business that configured none
/// of them, silently moves sales, fees, commissions and profit between days; refusing the request
/// is the only answer that cannot corrupt a financial report. The message names no business and no
/// actor: it is for an operator reading the server log, and the HTTP boundary answers the caller
/// with a generic 500 carrying none of it.
/// </summary>
public sealed class BusinessTimeZoneUnavailableException : InvalidOperationException
{
    public BusinessTimeZoneUnavailableException(string? timeZoneId)
        : base(Describe(timeZoneId))
    {
        TimeZoneId = timeZoneId;
    }

    /// <summary>The unusable id, or <see langword="null"/> when no business was resolved at all.</summary>
    public string? TimeZoneId { get; }

    private static string Describe(string? timeZoneId) =>
        timeZoneId is null
            ? "No business time zone has been resolved for this request, so no business date can be "
                + "derived. Business dates are only available once the current business has been resolved."
            : $"The current business's time zone '{timeZoneId}' is not an IANA identifier this host's "
                + "time-zone database can resolve, so no business date can be derived for it.";
}
