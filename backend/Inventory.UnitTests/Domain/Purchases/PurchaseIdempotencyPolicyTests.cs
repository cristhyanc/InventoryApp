using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// The rules about the idempotency key a purchase create may carry (issue #1247).
/// </summary>
public class PurchaseIdempotencyPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_key_is_no_key_at_all(string? submitted)
    {
        // Which is what keeps a form field nobody filled in from claiming the empty string for a
        // whole business, and keeps every client that predates this issue working.
        Assert.Null(PurchaseIdempotencyPolicy.Normalize(submitted));
        Assert.True(PurchaseIdempotencyPolicy.IsWithinLimit(submitted));
    }

    [Fact]
    public void A_key_is_held_to_trimmed()
    {
        Assert.Equal("abc-123", PurchaseIdempotencyPolicy.Normalize("  abc-123 "));
    }

    [Fact]
    public void A_key_of_the_maximum_length_is_accepted_and_one_character_more_is_not()
    {
        var longest = new string('k', PurchaseIdempotencyPolicy.MaximumKeyLength);

        Assert.True(PurchaseIdempotencyPolicy.IsWithinLimit(longest));
        Assert.False(PurchaseIdempotencyPolicy.IsWithinLimit(longest + "k"));
    }

    [Fact]
    public void The_limit_is_measured_after_trimming()
    {
        // Surrounding whitespace is never stored, so it must not make an acceptable key too long.
        var longest = new string('k', PurchaseIdempotencyPolicy.MaximumKeyLength);

        Assert.True(PurchaseIdempotencyPolicy.IsWithinLimit($"  {longest}  "));
    }

    /// <summary>
    /// The sentence is the repository owner's decision of 14 November 2026, and the form shows it
    /// verbatim: a reused key means the purchase on screen was never saved and the one that was
    /// saved is something else, which only a reload can show.
    /// </summary>
    [Fact]
    public void The_reused_key_refusal_asks_the_operator_to_reload()
    {
        Assert.Equal(
            "This purchase was already saved with different details. Reload the page.",
            PurchaseIdempotencyPolicy.KeyReusedMessage);
    }
}
