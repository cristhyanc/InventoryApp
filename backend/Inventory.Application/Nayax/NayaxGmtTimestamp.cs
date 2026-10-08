using System.Globalization;

namespace Inventory.Application.Nayax;

/// <summary>
/// The one way a Nayax timestamp that the Nayax contract documents as GMT is turned into a true UTC
/// instant (issue #471), shared by the live Lynx JSON boundary
/// (<see cref="NayaxGmtTimestampJsonConverter"/>, used by
/// <see cref="NayaxLastSalesReport.AuthorizationDateTimeGmt"/>) and the uploaded-export reader's
/// <c>AuthorizationDateTimeGMT</c> column.
///
/// The rule the Nayax contract establishes, and the reason this type exists:
/// <list type="bullet">
///   <item>A value carrying <strong>no designator and no offset</strong> is UTC, because the field
///   itself is documented as GMT. The published <c>GET /v1/machines/{MachineID}/lastSales</c> contract
///   declares <c>AuthorizationDateTimeGMT</c> as "The date and time when the transaction was
///   authorized, in GMT", and the portal's own live sample response prints it offset-free
///   (<c>"2026-02-08T09:31:51.817"</c>) while the same item's machine-local
///   <c>MachineAuthorizationTime</c> is two hours later. The UTC meaning therefore comes from the
///   field contract - never from a trailing <c>Z</c>, and never from the time zone of the host the
///   process happens to run on.</item>
///   <item>A value carrying <c>Z</c> or an explicit offset keeps its <strong>physical
///   instant</strong>, converted to UTC exactly once. <see cref="DateTimeOffset.UtcDateTime"/> is
///   offset-aware and idempotent, so re-reading a transaction - which the rolling last-sales window
///   does on every refresh, and a re-uploaded export does on every upload - can never shift it a
///   second time.</item>
///   <item>A value that is absent, blank or unreadable has <strong>no instant at all</strong>. It
///   never becomes <see cref="DateTime.MinValue"/>, the current date, or the machine-local wall
///   clock: a caller that has no authoritative instant must refuse the sale rather than guess one.</item>
/// </list>
///
/// Scope is deliberate. This parser is applied only to fields Nayax documents as GMT, never to the
/// machine-local <c>MachineAuthorizationTime</c> (where an offset would contradict what the field
/// means), and never to date-only business values such as reimbursement coverage dates or report
/// range bounds, which are calendar dates rather than instants.
/// </summary>
public static class NayaxGmtTimestamp
{
    /// <summary>
    /// Parses a documented-GMT text value into an offset-free-means-UTC
    /// <see cref="DateTimeOffset"/> normalized to a zero offset, or <c>null</c> when the value is
    /// absent, blank or unreadable.
    ///
    /// <see cref="DateTimeStyles.AssumeUniversal"/> is what makes an offset-free value UTC instead of
    /// host-local, and <see cref="DateTimeStyles.AdjustToUniversal"/> converts an explicitly offset
    /// value to the same physical instant rather than shifting it again.
    /// <see cref="CultureInfo.InvariantCulture"/> keeps the reading independent of the host's locale.
    /// </summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The same parse as <see cref="Parse(string?)"/>, answered as the true UTC instant
    /// (<see cref="DateTimeKind.Utc"/>) that persistence stores.
    /// </summary>
    public static DateTime? ParseInstantUtc(string? value) => Parse(value)?.UtcDateTime;
}
