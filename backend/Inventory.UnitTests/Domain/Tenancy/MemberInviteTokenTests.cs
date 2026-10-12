using System.Security.Cryptography;
using System.Text;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The one representation of an invite token this application keeps (issue #531).
///
/// The hash has to be two things at once: one-way, so a stored row is not a usable link, and
/// deterministic, so the redemption of #532 can find an invite by hashing the token it was handed.
/// These tests pin both, and the agreed 128-bit entropy floor the creation path draws against.
/// </summary>
public class MemberInviteTokenTests
{
    private const string Token = "9Vb1mE0qf-kZ3rQyT7sXnJ2dLpA4cW8uB6hG5iO1yKc";

    [Fact]
    public void The_hash_is_the_tokens_sha256_as_lowercase_hex()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));

        var hash = MemberInviteToken.Hash(Token);

        Assert.Equal(expected, hash);
        Assert.Equal(MemberInviteToken.HashLength, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        // One-way: the stored value contains nothing of the token it came from.
        Assert.DoesNotContain(Token, hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deterministic for the same token - which is what a redemption lookup depends on - and
    /// different for a token that differs by one character.
    /// </summary>
    [Fact]
    public void The_hash_is_deterministic_and_distinguishes_tokens()
    {
        Assert.Equal(MemberInviteToken.Hash(Token), MemberInviteToken.Hash(Token));
        Assert.NotEqual(MemberInviteToken.Hash(Token), MemberInviteToken.Hash(Token[..^1] + "d"));
        // Case matters: the token is an opaque random string, not an identifier to be normalised.
        Assert.NotEqual(MemberInviteToken.Hash(Token), MemberInviteToken.Hash(Token.ToUpperInvariant()));
    }

    /// <summary>
    /// A blank token is a programming error, not a caller's input, and it must not hash into a
    /// perfectly valid-looking lookup key.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_cannot_be_hashed(string token) =>
        Assert.Throws<ArgumentException>(() => MemberInviteToken.Hash(token));

    [Fact]
    public void A_null_token_cannot_be_hashed() =>
        Assert.Throws<ArgumentNullException>(() => MemberInviteToken.Hash(null!));

    [Fact]
    public void The_generated_token_is_at_least_the_agreed_minimum_entropy()
    {
        Assert.Equal(16, MemberInviteToken.MinimumEntropyBytes);
        Assert.True(
            MemberInviteToken.EntropyBytes >= MemberInviteToken.MinimumEntropyBytes,
            "The generated invite token must carry at least the agreed 128 bits of entropy.");
    }
}
