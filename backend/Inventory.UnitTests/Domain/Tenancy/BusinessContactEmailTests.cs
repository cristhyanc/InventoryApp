using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The contact-address format rule of issue #548. It exists to catch the mistake a person makes
/// while typing - a missing domain, a stray space, two addresses in one field - and deliberately
/// claims nothing more: a well-formed address is not a reachable one.
/// </summary>
public class BusinessContactEmailTests
{
    [Theory]
    [InlineData("owner@example.com")]
    [InlineData("owner.name@example.com")]
    [InlineData("owner+vending@example.com.au")]
    [InlineData("o@e.co")]
    [InlineData("Owner@Example.com")]
    public void A_well_formed_address_is_accepted_unchanged(string submitted)
    {
        Assert.True(BusinessContactEmail.TryNormalize(submitted, out var normalized));
        Assert.Equal(submitted, normalized);
    }

    /// <summary>
    /// Surrounding whitespace is the only thing removed. The case of an address is meaningful to
    /// its local part, so nothing is lower-cased on the way through.
    /// </summary>
    [Fact]
    public void Surrounding_whitespace_is_trimmed_and_nothing_else_is_changed()
    {
        Assert.True(BusinessContactEmail.TryNormalize("  Owner@Example.COM \t\n", out var normalized));
        Assert.Equal("Owner@Example.COM", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner")]
    [InlineData("owner@")]
    [InlineData("@example.com")]
    [InlineData("owner@example")]
    [InlineData("owner@.com")]
    [InlineData("owner@example.")]
    [InlineData("owner@example..com")]
    [InlineData("owner@@example.com")]
    [InlineData("owner@example.com, second@example.com")]
    [InlineData("own er@example.com")]
    [InlineData("owner@exa mple.com")]
    [InlineData("owner@exam\nple.com")]
    public void Anything_that_cannot_be_a_single_address_is_refused(string? submitted)
    {
        Assert.False(BusinessContactEmail.TryNormalize(submitted, out var normalized));
        Assert.Null(normalized);
    }

    /// <summary>
    /// 254 characters is the longest an address can be in an SMTP envelope, so a longer value
    /// cannot be deliverable and is refused rather than truncated - a cut-off address is a wrong
    /// address, not a shorter one.
    /// </summary>
    [Fact]
    public void An_address_longer_than_an_SMTP_envelope_allows_is_refused()
    {
        const string domain = "@example.com";
        var longest = new string('a', BusinessContactEmail.MaxLength - domain.Length) + domain;
        var tooLong = new string('a', BusinessContactEmail.MaxLength - domain.Length + 1) + domain;

        Assert.Equal(BusinessContactEmail.MaxLength, longest.Length);
        Assert.True(BusinessContactEmail.TryNormalize(longest, out _));
        Assert.False(BusinessContactEmail.TryNormalize(tooLong, out _));
    }
}
