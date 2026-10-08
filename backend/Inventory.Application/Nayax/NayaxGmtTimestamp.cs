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
///   <item>Only the <strong>documented date-time shapes</strong> are readable, and anything else is
///   unreadable rather than interpreted. The field is declared <c>string&lt;date-time&gt;</c>, so a
///   value that is merely <em>parseable</em> but is not a date-time - a date-only
///   <c>2026-10-07</c>, an ambiguous <c>07/10/2026</c> whose day and month cannot be told apart, a
///   locale or RFC 1123 rendering - must fail closed. Accepting one would invent an instant
///   (midnight UTC, or the wrong calendar day) from a payload that never stated one, and a financial
///   record carrying an invented instant is worse than a sale the rolling window offers again.</item>
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
    /// The complete set of date-time shapes a documented-GMT value may carry. Each is an ISO 8601
    /// calendar date, a date/time separator, a 24-hour time of day to at least the second, and
    /// optionally a <c>Z</c> designator or an explicit <c>±HH:mm</c> offset. Nothing else is read.
    ///
    /// <list type="bullet">
    ///   <item><c>.FFFFFFF</c> makes fractional seconds optional and accepts one to seven digits, so
    ///   every rendering the Nayax portal's own live samples print is covered - <c>.817</c>,
    ///   <c>.46</c> and <c>.5</c> alike - and a value with no fractional part is read as well.</item>
    ///   <item>The time of day is required, which is what refuses a date-only value: <c>2026-10-07</c>
    ///   does not state an instant, and reading it as midnight UTC would invent one.</item>
    ///   <item>Both the ISO <c>T</c> separator and a single space are accepted, because the same
    ///   parser also reads the uploaded export's <c>AuthorizationDateTimeGMT</c> column, which has no
    ///   published contract and has always been read leniently for unambiguous ISO text. A space
    ///   changes nothing about which instant the value names.</item>
    ///   <item>A slash-separated or month-name date is never accepted. <c>07/10/2026</c> is 7 October
    ///   in Australia and 10 July in the United States, and no payload field states which the writer
    ///   meant.</item>
    ///   <item><c>zzz</c> reads a signed hours-and-minutes offset with or without its colon
    ///   (<c>+11:00</c> and <c>+1100</c> name the same instant), but not an hours-only <c>+11</c>:
    ///   half-hour and three-quarter-hour zones exist, so a missing minutes component is refused
    ///   rather than assumed to be <c>:00</c>.</item>
    /// </list>
    /// </summary>
    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
    ];

    /// <summary>
    /// <see cref="DateTimeStyles.AssumeUniversal"/> is what makes an offset-free value UTC instead of
    /// host-local, and <see cref="DateTimeStyles.AdjustToUniversal"/> converts an explicitly offset
    /// value to the same physical instant rather than shifting it again.
    /// </summary>
    private const DateTimeStyles GmtStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Parses a documented-GMT text value into an offset-free-means-UTC
    /// <see cref="DateTimeOffset"/> normalized to a zero offset, or <c>null</c> when the value is
    /// absent, blank, or not one of the <see cref="AcceptedFormats"/> this GMT field is documented to
    /// carry.
    ///
    /// The parse is exact rather than <c>DateTimeOffset.TryParse</c>, so a
    /// malformed-but-parseable value fails closed instead of becoming a quietly invented instant.
    /// <see cref="CultureInfo.InvariantCulture"/> keeps the reading independent of the host's locale,
    /// and surrounding whitespace is not information, so it is trimmed before the comparison.
    /// </summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParseExact(
            value.Trim(),
            AcceptedFormats,
            CultureInfo.InvariantCulture,
            GmtStyles,
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
