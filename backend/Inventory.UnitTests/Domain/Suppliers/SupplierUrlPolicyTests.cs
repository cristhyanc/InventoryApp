using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;
using Xunit;

namespace Inventory.UnitTests.Domain.Suppliers;

/// <summary>
/// The shared optional-website rule of issue #848, which issue #847 is to reuse for a business's own
/// suppliers: absolute http(s), at most 2048 characters, blank means no address at all.
/// </summary>
public class SupplierUrlPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_blank_address_is_no_address_rather_than_a_refusal(string? url) =>
        Assert.Null(SupplierUrlPolicy.Normalise(url));

    [Theory]
    [InlineData("https://www.coles.com.au", "https://www.coles.com.au")]
    [InlineData("  https://www.coles.com.au  ", "https://www.coles.com.au")]
    [InlineData("http://example.test/path?q=1#top", "http://example.test/path?q=1#top")]
    public void An_absolute_http_address_is_kept_as_typed_once_trimmed(string url, string expected) =>
        Assert.Equal(expected, SupplierUrlPolicy.Normalise(url));

    /// <summary>
    /// A relative address is refused rather than guessed at: a stored link that resolves against
    /// whichever page rendered it is not an address.
    /// </summary>
    [Theory]
    [InlineData("www.coles.com.au")]
    [InlineData("coles.com.au")]
    [InlineData("/suppliers/coles")]
    [InlineData("coles")]
    public void A_relative_address_is_refused(string url)
    {
        var error = Assert.Throws<DomainValidationException>(() => SupplierUrlPolicy.Normalise(url));

        Assert.Equal(SupplierUrlPolicy.InvalidMessage, error.Message);
    }

    /// <summary>
    /// The value is rendered as a link a person clicks, so http and https are the only two schemes
    /// accepted - a scheme that executes or embeds is refused at the boundary rather than escaped
    /// later.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example.test")]
    [InlineData("HTTPS:/malformed")]
    public void Any_other_scheme_is_refused(string url)
    {
        var error = Assert.Throws<DomainValidationException>(() => SupplierUrlPolicy.Normalise(url));

        Assert.Equal(SupplierUrlPolicy.InvalidMessage, error.Message);
    }

    /// <summary>The boundary: exactly 2048 characters is accepted, 2049 is not.</summary>
    [Fact]
    public void The_length_limit_is_measured_on_the_trimmed_value()
    {
        var atTheLimit = AddressOfLength(SupplierUrlPolicy.MaxLength);
        Assert.Equal(SupplierUrlPolicy.MaxLength, atTheLimit.Length);
        Assert.Equal(atTheLimit, SupplierUrlPolicy.Normalise($"  {atTheLimit}  "));

        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierUrlPolicy.Normalise(AddressOfLength(SupplierUrlPolicy.MaxLength + 1)));

        Assert.Equal(SupplierUrlPolicy.TooLongMessage, error.Message);
    }

    /// <summary>
    /// A scheme this rule accepts is still only one half of the answer: an over-long absolute
    /// address is refused for its length, which is the fact the caller can act on.
    /// </summary>
    [Fact]
    public void An_over_long_address_is_refused_for_its_length_rather_than_its_shape()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierUrlPolicy.Normalise(new string('x', SupplierUrlPolicy.MaxLength + 1)));

        Assert.Equal(SupplierUrlPolicy.TooLongMessage, error.Message);
    }

    [Theory]
    [InlineData("https://example.test", true)]
    [InlineData("http://example.test", true)]
    [InlineData("example.test", false)]
    [InlineData(null, false)]
    public void The_question_the_normalisation_asks_is_exposed_unchanged(string? url, bool expected) =>
        Assert.Equal(expected, SupplierUrlPolicy.IsAbsoluteHttpUrl(url));

    private static string AddressOfLength(int length)
    {
        const string Prefix = "https://example.test/";
        return Prefix + new string('a', length - Prefix.Length);
    }
}
