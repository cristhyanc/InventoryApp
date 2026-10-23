namespace Inventory.Application.Suppliers;

/// <summary>
/// The one rule for a supplier's optional website URL (issue #847).
///
/// <para>It lives here, in the Application layer, rather than as a validation attribute on the
/// request DTO, so that every write path runs it: a DTO attribute only guards the one contract it
/// is written on, and a stored URL is rendered as a link, which makes the accepted scheme a
/// security boundary rather than a formatting preference. Only an absolute <c>http</c> or
/// <c>https</c> address with a host is accepted; <c>javascript:</c>, <c>data:</c>, <c>file:</c>,
/// <c>ftp:</c>, a relative path and a scheme-less host name are all refused.</para>
///
/// <para>The rule is mirrored character for character in the Angular form and link rendering
/// (<c>components/suppliers/supplier-url.ts</c>), as defence in depth and so an operator sees the
/// same message before the round trip as after it.</para>
///
/// <para>Nothing here fetches, resolves or follows the address. The server stores text.</para>
/// </summary>
public static class SupplierUrlPolicy
{
    /// <summary>
    /// The longest URL that may be stored, matching the <c>Suppliers.Url</c> column's length.
    /// </summary>
    public const int MaxLength = 2048;

    /// <summary>The field-level message for a value that is not an absolute http(s) address.</summary>
    public const string InvalidMessage =
        "Website URL must be a complete web address starting with http:// or https://.";

    /// <summary>The field-level message for a value longer than <see cref="MaxLength"/>.</summary>
    public const string TooLongMessage = "Website URL must be 2048 characters or fewer.";

    /// <summary>
    /// Normalizes a submitted website URL. A <c>null</c>, empty or whitespace-only value is
    /// accepted as "no website" and normalizes to <c>null</c>; anything else is trimmed and must be
    /// an absolute <c>http</c>/<c>https</c> URI with a non-empty host, within
    /// <see cref="MaxLength"/> characters.
    /// </summary>
    /// <param name="value">The submitted value, as typed.</param>
    /// <param name="normalized">The value to store, or <c>null</c> when none was supplied or the value was refused.</param>
    /// <param name="error">The field-level message to report, or <c>null</c> when the value was accepted.</param>
    /// <returns><c>true</c> when the value was accepted.</returns>
    public static bool TryNormalize(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();

        // Length first, so an over-long value is reported against the limit the caller can act on
        // rather than against whatever the parser made of it.
        if (trimmed.Length > MaxLength)
        {
            error = TooLongMessage;
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !IsWebScheme(uri.Scheme) ||
            string.IsNullOrEmpty(uri.Host))
        {
            error = InvalidMessage;
            return false;
        }

        // The operator's own text, trimmed and nothing else: re-serialising through Uri would
        // re-encode the path or append a trailing slash, storing a value nobody typed.
        normalized = trimmed;
        return true;
    }

    private static bool IsWebScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
