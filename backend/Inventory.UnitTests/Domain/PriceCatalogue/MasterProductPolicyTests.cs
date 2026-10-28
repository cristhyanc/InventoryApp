using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of one shared master product (issue #923): the brand, the name, the size with its fixed
/// unit list, and the normalised key that decides whether two descriptions are one product.
///
/// <para>The key is what the parent issue (#817) is about - one product is looked up once for every
/// business - so the cases that matter are the ones where two spellings must fold into one key, and
/// the ones where two different products must not.</para>
/// </summary>
public class MasterProductPolicyTests
{
    #region Size

    [Theory]
    [InlineData(375)]
    [InlineData(1.25)]
    [InlineData(0.001)]
    public void A_positive_size_is_stored_as_submitted(decimal size) =>
        Assert.Equal(size, MasterProductPolicy.NormaliseSize(size, MasterProductSizeUnit.Millilitre));

    /// <summary>
    /// Zero is refused as firmly as a negative value. A zero size is not a smaller product: it is a
    /// denominator that makes a per-unit price infinite, and it would compare as cheaper than every
    /// real product.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-375)]
    public void A_size_that_is_not_positive_is_refused(decimal size)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseSize(size, MasterProductSizeUnit.Millilitre));

        Assert.Equal(MasterProductPolicy.SizeNotPositiveMessage, refusal.Message);
    }

    /// <summary>
    /// A size finer than the stored scale is refused rather than rounded. Rounding would record a
    /// product nobody described, and a quietly rounded denominator makes every per-unit price derived
    /// from it wrong in the same direction.
    /// </summary>
    [Fact]
    public void A_size_finer_than_the_stored_scale_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseSize(374.9999m, MasterProductSizeUnit.Millilitre));

        Assert.Equal(MasterProductPolicy.SizeTooPreciseMessage, refusal.Message);
    }

    /// <summary>
    /// The unit list is closed and checked: an enum is a compile-time constraint and not a
    /// validation, and a deserialized JSON number binds an arbitrary integer.
    /// </summary>
    [Fact]
    public void An_undeclared_size_unit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseSize(375m, (MasterProductSizeUnit)42));

        Assert.Equal(MasterProductPolicy.UnknownSizeUnitMessage, refusal.Message);
    }

    /// <summary>
    /// The fixed unit list, asserted as the codes a person reads, so adding a sixth unit is a
    /// deliberate change to this test as well as to the vocabulary.
    /// </summary>
    [Fact]
    public void The_size_unit_vocabulary_is_ml_l_g_kg_and_each() =>
        Assert.Equal(
            ["ml", "l", "g", "kg", "each"],
            Enum.GetValues<MasterProductSizeUnit>()
                .Select(MasterProductPolicy.SizeUnitCode)
                .ToArray());

    #endregion

    #region Brand and name

    [Fact]
    public void A_brand_and_a_name_are_trimmed()
    {
        Assert.Equal("Coca-Cola", MasterProductPolicy.NormaliseBrand("  Coca-Cola "));
        Assert.Equal("Classic", MasterProductPolicy.NormaliseName(" Classic  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_master_product_without_a_brand_is_refused(string? brand)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseBrand(brand));

        Assert.Equal(MasterProductPolicy.BrandRequiredMessage, refusal.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_master_product_without_a_name_is_refused(string? name)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseName(name));

        Assert.Equal(MasterProductPolicy.NameRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_brand_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseBrand(
                new string('b', MasterProductPolicy.MaxBrandLength + 1)));

        Assert.Equal(MasterProductPolicy.BrandTooLongMessage, refusal.Message);
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormaliseName(
                new string('n', MasterProductPolicy.MaxNameLength + 1)));

        Assert.Equal(MasterProductPolicy.NameTooLongMessage, refusal.Message);
    }

    [Fact]
    public void A_brand_and_a_name_of_exactly_the_limit_are_accepted()
    {
        var brand = new string('b', MasterProductPolicy.MaxBrandLength);
        var name = new string('n', MasterProductPolicy.MaxNameLength);

        Assert.Equal(brand, MasterProductPolicy.NormaliseBrand(brand));
        Assert.Equal(name, MasterProductPolicy.NormaliseName(name));
    }

    #endregion

    #region The normalised key

    /// <summary>
    /// The key reads as the description it came from, which is what makes a stored key diagnosable
    /// from the database rather than only comparable.
    /// </summary>
    [Fact]
    public void The_key_is_the_folded_brand_name_size_and_unit_code() =>
        Assert.Equal(
            "9:COCA-COLA|7:CLASSIC|3:375|2:ML",
            MasterProductPolicy.NormalisedKey(
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre));

    /// <summary>
    /// Two different descriptions stay two master products when the only difference is which side of
    /// a part boundary a separator falls on. Brand and name are free text, so a brand ending in the
    /// separator character is input this key has to survive: under the escaping rule this replaced,
    /// <c>("Cola|", "Classic")</c> and <c>("Cola", "|Classic")</c> folded to one key, and the unique
    /// index on the stored key would have refused the second description as a duplicate of the first.
    /// </summary>
    [Fact]
    public void A_brand_ending_in_the_separator_is_not_a_name_beginning_with_it() =>
        Assert.NotEqual(
            MasterProductPolicy.NormalisedKey("Cola|", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            MasterProductPolicy.NormalisedKey("Cola", "|Classic", 375m, MasterProductSizeUnit.Millilitre));

    /// <summary>
    /// Case and spacing do not make two master products of one. This is the deduplication the parent
    /// issue exists for, at the master-product end of it.
    /// </summary>
    [Fact]
    public void Two_descriptions_differing_only_in_case_and_spacing_are_one_key() =>
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            MasterProductPolicy.NormalisedKey("  coca-cola", "cla ssic ", 375m, MasterProductSizeUnit.Millilitre));

    /// <summary>
    /// Three equal decimals with three different default spellings produce one key. Without the
    /// invariant <c>0.###</c> format, <c>375</c>, <c>375.0</c> and <c>375.000</c> would be three
    /// master products for one can.
    /// </summary>
    [Fact]
    public void A_size_with_trailing_zeros_is_the_same_key()
    {
        var key = MasterProductPolicy.NormalisedKey(
            "Coca-Cola",
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre);

        Assert.Equal(
            key,
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375.000m, MasterProductSizeUnit.Millilitre));
        Assert.Equal(
            key,
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375.0m, MasterProductSizeUnit.Millilitre));
    }

    /// <summary>
    /// And the same number in a different unit is a different product: 375 ml of something is not
    /// 375 g of it.
    /// </summary>
    [Fact]
    public void The_same_size_in_a_different_unit_is_a_different_key() =>
        Assert.NotEqual(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Gram));

    /// <summary>
    /// A description the policy would refuse never produces a key. A key for a row that could not be
    /// stored is a key nothing can be compared against.
    /// </summary>
    [Fact]
    public void A_refused_description_produces_no_key() =>
        Assert.Throws<DomainValidationException>(
            () => MasterProductPolicy.NormalisedKey(
                "Coca-Cola",
                "Classic",
                0m,
                MasterProductSizeUnit.Millilitre));

    /// <summary>
    /// The column bound is computed from the part limits, so widening a part cannot leave the stored
    /// key column too narrow for the keys it must hold.
    /// </summary>
    [Fact]
    public void The_key_bound_covers_the_longest_description_the_policy_accepts()
    {
        var key = MasterProductPolicy.NormalisedKey(
            new string('|', MasterProductPolicy.MaxBrandLength),
            new string('|', MasterProductPolicy.MaxNameLength),
            999.999m,
            MasterProductSizeUnit.Millilitre);

        Assert.True(
            key.Length <= MasterProductPolicy.MaxNormalisedKeyLength,
            $"The key is {key.Length} characters and the column holds "
                + $"{MasterProductPolicy.MaxNormalisedKeyLength}.");
    }

    #endregion
}
