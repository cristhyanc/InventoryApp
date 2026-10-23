using Inventory.Application.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Application.Suppliers;

/// <summary>
/// The one rule for a supplier's optional website URL (issue #847). It is an Application-layer
/// rule rather than a DTO attribute so every write path runs it, and the Angular form mirrors it
/// character for character in <c>components/suppliers/supplier-url.ts</c>.
///
/// <para>The rejections matter more than the acceptances here: the stored value is rendered as a
/// link, so a scheme that is not <c>http</c> or <c>https</c> - <c>javascript:</c> above all - must
/// never reach the database in the first place.</para>
/// </summary>
public class SupplierUrlPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void An_absent_or_blank_url_is_accepted_as_null(string? value)
    {
        Assert.True(SupplierUrlPolicy.TryNormalize(value, out var normalized, out var error));

        Assert.Null(normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://coles.com.au", "http://coles.com.au")]
    [InlineData("https://coles.com.au", "https://coles.com.au")]
    [InlineData("  https://coles.com.au/trade?ref=1#top  ", "https://coles.com.au/trade?ref=1#top")]
    [InlineData("HTTPS://COLES.COM.AU", "HTTPS://COLES.COM.AU")]
    [InlineData("Http://coles.com.au", "Http://coles.com.au")]
    [InlineData("https://sub.domain.example.com:8443/path", "https://sub.domain.example.com:8443/path")]
    public void A_valid_absolute_http_url_is_accepted_and_trimmed(string value, string expected)
    {
        Assert.True(SupplierUrlPolicy.TryNormalize(value, out var normalized, out var error));

        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    /// <summary>
    /// The scheme is compared case-insensitively, but the stored text is the operator's own - the
    /// rule trims, and nothing else. Re-serialising through <see cref="Uri"/> would silently
    /// re-encode a path or append a trailing slash, which is a different value from the one the
    /// person typed.
    /// </summary>
    [Fact]
    public void An_accepted_url_is_stored_exactly_as_typed_apart_from_the_trim()
    {
        Assert.True(SupplierUrlPolicy.TryNormalize(
            " https://example.test/a%20b/../c ", out var normalized, out _));

        Assert.Equal("https://example.test/a%20b/../c", normalized);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example.com")]
    [InlineData("mailto:buyer@example.test")]
    [InlineData("//coles.com.au")]
    [InlineData("coles.com.au")]
    [InlineData("www.coles.com.au/trade")]
    [InlineData("/suppliers/1")]
    [InlineData("../suppliers")]
    [InlineData("https:///no-host")]
    [InlineData("http://")]
    [InlineData("not a url at all")]
    public void A_url_that_is_not_an_absolute_http_or_https_address_is_rejected(string value)
    {
        Assert.False(SupplierUrlPolicy.TryNormalize(value, out var normalized, out var error));

        Assert.Null(normalized);
        Assert.Equal(SupplierUrlPolicy.InvalidMessage, error);
    }

    [Fact]
    public void A_url_of_exactly_the_maximum_length_is_accepted()
    {
        var value = UrlOfLength(SupplierUrlPolicy.MaxLength);

        Assert.True(SupplierUrlPolicy.TryNormalize(value, out var normalized, out var error));

        Assert.Equal(SupplierUrlPolicy.MaxLength, normalized!.Length);
        Assert.Null(error);
    }

    /// <summary>
    /// The length is measured after the trim, so surrounding whitespace never costs the operator a
    /// character of a URL that fits.
    /// </summary>
    [Fact]
    public void Length_is_measured_after_trimming()
    {
        var value = "  " + UrlOfLength(SupplierUrlPolicy.MaxLength) + "  ";

        Assert.True(SupplierUrlPolicy.TryNormalize(value, out var normalized, out _));

        Assert.Equal(SupplierUrlPolicy.MaxLength, normalized!.Length);
    }

    [Fact]
    public void A_url_one_character_over_the_maximum_length_is_rejected_as_too_long()
    {
        var value = UrlOfLength(SupplierUrlPolicy.MaxLength + 1);

        Assert.False(SupplierUrlPolicy.TryNormalize(value, out var normalized, out var error));

        Assert.Null(normalized);
        Assert.Equal(SupplierUrlPolicy.TooLongMessage, error);
    }

    /// <summary>
    /// An over-long value that is also not an http(s) address is reported as too long: the length
    /// is checked first so the message names the limit the caller can actually act on.
    /// </summary>
    [Fact]
    public void An_over_long_value_is_reported_as_too_long_before_the_scheme_is_considered()
    {
        var value = "javascript:" + new string('a', SupplierUrlPolicy.MaxLength);

        Assert.False(SupplierUrlPolicy.TryNormalize(value, out _, out var error));

        Assert.Equal(SupplierUrlPolicy.TooLongMessage, error);
    }

    [Fact]
    public void The_maximum_length_is_the_published_2048_characters()
    {
        Assert.Equal(2048, SupplierUrlPolicy.MaxLength);
    }

    /// <summary>An <c>https</c> URL padded out to exactly <paramref name="length"/> characters.</summary>
    private static string UrlOfLength(int length)
    {
        const string prefix = "https://coles.com.au/";
        return prefix + new string('a', length - prefix.Length);
    }
}
