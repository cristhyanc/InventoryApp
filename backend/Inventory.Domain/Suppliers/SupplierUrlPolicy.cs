using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Suppliers;

/// <summary>
/// The one rule for an optional supplier website address (issue #848, and the rule issue #847 is to
/// reuse for <c>Supplier.Url</c>).
///
/// <para>It is written here, once, rather than in each feature that accepts a URL, because the two
/// features that hold one - the platform's supplier presets and a business's own suppliers - copy
/// values between each other (issue #835 child C), and a value one accepted that the other refuses
/// would be a copy that fails. At the time this shipped #847 had not merged, so this is the shared
/// place it is to use rather than a second rule beside it.</para>
///
/// <para><strong>What is accepted.</strong> A complete absolute <c>http</c> or <c>https</c> address,
/// at most <see cref="MaxLength"/> characters once trimmed. Blank - null, empty, or whitespace - is
/// not a refusal but an absence: it normalises to <see langword="null"/>, because "no website" is
/// the ordinary case and an empty string stored in a nullable column would be a second way of
/// spelling it.</para>
///
/// <para><strong>What is deliberately refused.</strong> A relative address (<c>example.com</c>,
/// <c>/suppliers</c>), because a stored link that resolves against whichever page rendered it is not
/// an address at all; and any other scheme - <c>javascript:</c>, <c>data:</c>, <c>file:</c>,
/// <c>ftp:</c> - because this value is rendered as a link a person clicks, and the only two schemes
/// that may be is the narrowest answer that still works. Nothing here fetches, resolves or verifies
/// the address: whether the site exists is not this rule's business (it is explicitly out of scope
/// of issue #848).</para>
/// </summary>
public static class SupplierUrlPolicy
{
    /// <summary>The longest accepted address, measured on the trimmed value.</summary>
    public const int MaxLength = 2048;

    /// <summary>What a caller who submitted something that is not an absolute http(s) address is told.</summary>
    public const string InvalidMessage =
        "A website address must be a complete http:// or https:// address.";

    /// <summary>What a caller who submitted an address longer than <see cref="MaxLength"/> is told.</summary>
    public const string TooLongMessage =
        "A website address must be 2048 characters or fewer.";

    /// <summary>
    /// The address as it is to be stored: trimmed, or <see langword="null"/> when the caller
    /// submitted nothing at all.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The value is not an absolute http(s) address, or is longer than <see cref="MaxLength"/>.
    /// Nothing about the caller's input is echoed back in the message.
    /// </exception>
    public static string? Normalise(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();

        // Length is judged before the shape, so a megabyte of text is refused for what it is rather
        // than parsed first.
        if (trimmed.Length > MaxLength)
        {
            throw new DomainValidationException(TooLongMessage);
        }

        if (!IsAbsoluteHttpUrl(trimmed))
        {
            throw new DomainValidationException(InvalidMessage);
        }

        return trimmed;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is an absolute <c>http</c>/<c>https</c> address. Exposed so a
    /// caller that wants to ask rather than to refuse - a read of data stored before this rule
    /// existed, for example - asks the same question the normalisation asks.
    /// </summary>
    public static bool IsAbsoluteHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
