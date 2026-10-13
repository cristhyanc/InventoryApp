using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The ABN check of issue #540. An ABN carries a modulus-89 check digit, so a mistyped or invented
/// number is detectable without contacting the register - and because the number ends up on tax
/// invoices and in the GST accounting aid, storing one that cannot be an ABN is storing a wrong
/// fact about the business.
///
/// The published numbers used here are the Australian Business Register's own documented examples
/// and the ATO's published ABN, chosen so the test proves the real algorithm rather than one this
/// code invented.
/// </summary>
public class AustralianBusinessNumberTests
{
    /// <summary>The ABR's documented example ABN.</summary>
    private const string ValidAbn = "51824753556";

    [Theory]
    [InlineData("51824753556")]
    [InlineData("53004085616")]
    public void A_well_formed_ABN_is_valid(string abn)
    {
        Assert.True(AustralianBusinessNumber.IsValid(abn));
        Assert.True(AustralianBusinessNumber.TryNormalize(abn, out var normalized));
        Assert.Equal(abn, normalized);
    }

    /// <summary>
    /// The whole point of the check digit: a single transposed or altered digit makes the weighted
    /// sum indivisible by 89, so the number is refused rather than stored as somebody's ABN.
    /// </summary>
    [Theory]
    [InlineData("51824753557")]
    [InlineData("51824753565")]
    [InlineData("41824753556")]
    [InlineData("15824753556")]
    public void A_number_failing_the_check_digit_test_is_refused(string abn)
    {
        Assert.False(AustralianBusinessNumber.IsValid(abn));
        Assert.False(AustralianBusinessNumber.TryNormalize(abn, out var normalized));
        Assert.Null(normalized);
    }

    /// <summary>
    /// The leading digit has 1 subtracted from it before weighting. Without that step a number
    /// whose first digit is one too low would pass, which is exactly the error the subtraction
    /// exists to catch.
    /// </summary>
    [Fact]
    public void The_leading_digit_adjustment_is_applied()
    {
        // The same ten trailing digits behind a leading 4 instead of a 5 must be refused: only
        // the weighted leading digit tells the two apart, and only after the subtraction.
        Assert.True(AustralianBusinessNumber.IsValid(ValidAbn));
        Assert.False(AustralianBusinessNumber.IsValid("41824753556"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5182475355")]
    [InlineData("518247535567")]
    [InlineData("5182475355A")]
    [InlineData("51-824-753-556")]
    [InlineData("51.824.753.556")]
    [InlineData("ABN 51824753556")]
    public void Anything_that_is_not_eleven_digits_is_refused(string submitted)
    {
        Assert.False(AustralianBusinessNumber.IsValid(submitted));
        Assert.False(AustralianBusinessNumber.TryNormalize(submitted, out _));
    }

    /// <summary>
    /// A null ABN is not valid <em>as an ABN</em>. Whether an absent ABN is acceptable is the
    /// caller's own decision - it is an optional profile field - and answering "valid" here would
    /// let a null pass a check that is about digits.
    /// </summary>
    [Fact]
    public void A_null_value_is_not_an_ABN()
    {
        Assert.False(AustralianBusinessNumber.IsValid(null));
        Assert.False(AustralianBusinessNumber.TryNormalize(null, out _));
    }

    /// <summary>
    /// People type an ABN the way the register prints it. Spaces are removed on the way in, and
    /// the canonical digits-only value is what is stored, so one business cannot hold two
    /// spellings of its own ABN.
    /// </summary>
    [Theory]
    [InlineData("51 824 753 556")]
    [InlineData("  51824753556  ")]
    [InlineData("51824 753556")]
    public void A_submitted_ABN_may_carry_spaces_and_normalises_to_digits(string submitted)
    {
        Assert.True(AustralianBusinessNumber.TryNormalize(submitted, out var normalized));
        Assert.Equal(ValidAbn, normalized);

        // The spaced form is not the stored form, so it is not itself "valid" to store.
        Assert.False(AustralianBusinessNumber.IsValid(submitted));
    }

    /// <summary>
    /// Normalisation removes spaces; it never repairs anything else. A number with a letter or the
    /// wrong number of digits is refused rather than being salvaged into some other value.
    /// </summary>
    [Fact]
    public void Normalisation_repairs_nothing_but_spacing()
    {
        Assert.False(AustralianBusinessNumber.TryNormalize("51 824 753 55", out _));
        Assert.False(AustralianBusinessNumber.TryNormalize("51 824 753 55X", out _));
    }
}
