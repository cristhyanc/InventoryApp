using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// The rules about the supplier's receipt or invoice number recorded on a purchase (issue #1162):
/// what is stored for what was submitted, and what is too long to store at all.
/// </summary>
public class PurchaseInvoiceNumberPolicyTests
{
    [Theory]
    [InlineData(" INV-123 ", "INV-123")]
    [InlineData("INV-123", "INV-123")]
    [InlineData("\t INV-123\r\n", "INV-123")]
    public void A_submitted_number_is_stored_trimmed(string submitted, string stored) =>
        Assert.Equal(stored, PurchaseInvoiceNumberPolicy.Normalize(submitted));

    /// <summary>
    /// Nothing, spaces and a cleared field are one stored state - none - rather than three blanks a
    /// reader would have to tell apart.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void A_blank_number_is_stored_as_none(string? submitted) =>
        Assert.Null(PurchaseInvoiceNumberPolicy.Normalize(submitted));

    /// <summary>
    /// Only the surrounding whitespace goes: the case, the inner spacing and the punctuation are the
    /// supplier's own, so they are stored exactly as entered.
    /// </summary>
    [Fact]
    public void Nothing_inside_the_number_is_changed()
    {
        Assert.Equal("inv 123/A-9", PurchaseInvoiceNumberPolicy.Normalize("  inv 123/A-9  "));
        Assert.Equal("INV  123", PurchaseInvoiceNumberPolicy.Normalize("INV  123"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INV-123")]
    public void A_blank_or_ordinary_number_is_within_the_limit(string? submitted) =>
        Assert.True(PurchaseInvoiceNumberPolicy.IsWithinLimit(submitted));

    [Fact]
    public void A_number_of_exactly_the_maximum_length_is_within_the_limit() =>
        Assert.True(PurchaseInvoiceNumberPolicy.IsWithinLimit(new string('9', PurchaseInvoiceNumberPolicy.MaxLength)));

    /// <summary>
    /// The limit is measured on the trimmed value, so surrounding whitespace never turns an invoice
    /// number that fits into one that is refused.
    /// </summary>
    [Fact]
    public void Whitespace_around_a_number_of_the_maximum_length_does_not_make_it_too_long()
    {
        var padded = $"  {new string('9', PurchaseInvoiceNumberPolicy.MaxLength)}  ";

        Assert.True(PurchaseInvoiceNumberPolicy.IsWithinLimit(padded));
        Assert.Equal(PurchaseInvoiceNumberPolicy.MaxLength, PurchaseInvoiceNumberPolicy.Normalize(padded)!.Length);
    }

    [Fact]
    public void One_character_past_the_maximum_length_is_too_long() =>
        Assert.False(
            PurchaseInvoiceNumberPolicy.IsWithinLimit(new string('9', PurchaseInvoiceNumberPolicy.MaxLength + 1)));
}
