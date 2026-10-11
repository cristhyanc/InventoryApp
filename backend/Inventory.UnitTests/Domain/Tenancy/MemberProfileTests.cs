using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The display-only member profile (issue #524): what a validated token's <c>name</c> and
/// <c>preferred_username</c>/<c>email</c> claims become before anything is stored.
/// </summary>
public class MemberProfileTests
{
    [Fact]
    public void Both_claims_are_kept_as_the_token_reported_them()
    {
        var profile = MemberProfile.Create("Ada Lovelace", "ada@vending.example");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
        Assert.Equal("ada@vending.example", profile.Email);
        Assert.True(profile.HasAnyValue);
    }

    [Fact]
    public void Surrounding_whitespace_is_not_part_of_a_display_value()
    {
        var profile = MemberProfile.Create("  Ada Lovelace\t", " ada@vending.example ");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
        Assert.Equal("ada@vending.example", profile.Email);
    }

    /// <summary>
    /// Missing, empty and blank are one state: there is nothing to display either way, and
    /// storing a blank string would make "no name reported" look like "their name is nothing".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_missing_or_blank_claim_is_absent_rather_than_blank(string? value)
    {
        var profile = MemberProfile.Create(value, value);

        Assert.Null(profile.DisplayName);
        Assert.Null(profile.Email);
        Assert.False(profile.HasAnyValue);
        Assert.Equal(MemberProfile.None, profile);
    }

    [Fact]
    public void One_claim_on_its_own_is_enough_to_store()
    {
        Assert.True(MemberProfile.Create("Ada Lovelace", null).HasAnyValue);
        Assert.True(MemberProfile.Create(null, "ada@vending.example").HasAnyValue);
    }

    /// <summary>
    /// A value at the limit is stored; one character more is treated as absent rather than cut
    /// short. Truncating is the wrong answer for both halves - a cut-off email address is a wrong
    /// address, and half a name presented as somebody's name is wrong in the same way - and the
    /// limit is what bounds how much one token claim can write into the membership row.
    /// </summary>
    [Fact]
    public void A_value_longer_than_the_limit_is_absent_rather_than_truncated()
    {
        var atTheLimit = new string('a', MemberProfile.MaxLength);
        var overTheLimit = new string('a', MemberProfile.MaxLength + 1);

        Assert.Equal(atTheLimit, MemberProfile.Create(atTheLimit, atTheLimit).DisplayName);
        Assert.Equal(atTheLimit, MemberProfile.Create(atTheLimit, atTheLimit).Email);

        var refused = MemberProfile.Create(overTheLimit, overTheLimit);
        Assert.Null(refused.DisplayName);
        Assert.Null(refused.Email);
        Assert.False(refused.HasAnyValue);
    }

    /// <summary>
    /// The length limit is applied after trimming, so padding cannot push an otherwise acceptable
    /// value over it.
    /// </summary>
    [Fact]
    public void The_limit_is_measured_after_trimming()
    {
        var padded = "  " + new string('a', MemberProfile.MaxLength) + "  ";

        Assert.Equal(new string('a', MemberProfile.MaxLength), MemberProfile.Create(padded, null).DisplayName);
    }

    /// <summary>
    /// Two profiles carrying the same two values are the same profile. The refresh path compares
    /// them rather than rewriting a row that already matches the token.
    /// </summary>
    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(
            MemberProfile.Create("Ada Lovelace", "ada@vending.example"),
            MemberProfile.Create("Ada Lovelace", "ada@vending.example"));

        Assert.NotEqual(
            MemberProfile.Create("Ada Lovelace", "ada@vending.example"),
            MemberProfile.Create("Ada Lovelace", "ADA@vending.example"));
    }
}
