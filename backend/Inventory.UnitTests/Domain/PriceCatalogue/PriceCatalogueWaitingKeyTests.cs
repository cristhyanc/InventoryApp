using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The waiting key and the shared folding rule beneath it (issue #923, parent #817): what makes five
/// businesses' five "Coca-Cola Classic 375mL" products one search.
///
/// <para>The cases that matter are the two directions of one rule. Two spellings that differ only in
/// case or spacing must fold to one key, because that is the deduplication the parent issue exists
/// for; and two genuinely different pairs must not, because a collector that searched for the wrong
/// thing would price the wrong product.</para>
/// </summary>
public class PriceCatalogueWaitingKeyTests
{
    /// <summary>
    /// The key reads as the search text it came from, which is what makes a stored key diagnosable
    /// from the database.
    /// </summary>
    [Fact]
    public void The_key_is_the_folded_name_and_unit_label() =>
        Assert.Equal(
            "COCA-COLACLASSIC|375ML",
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"));

    /// <summary>
    /// The deduplication rule itself: case and spacing are ignored. These are five spellings of one
    /// product, as five businesses' catalogues would legitimately hold them.
    /// </summary>
    [Theory]
    [InlineData("Coca-Cola Classic", "375mL")]
    [InlineData("coca-cola classic", "375ml")]
    [InlineData("COCA-COLA   CLASSIC", "375 ML")]
    [InlineData("  Coca-ColaClassic  ", " 375mL ")]
    [InlineData("Coca-Cola\tClassic", "375 mL")]
    public void Spellings_that_differ_only_in_case_and_spacing_are_one_key(
        string searchName,
        string unitLabel) =>
        Assert.Equal(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            PriceCatalogueWaitingKey.For(searchName, unitLabel));

    /// <summary>
    /// Nothing but case and spacing is folded. A hyphen is a character of the name, and merging
    /// "Coca-Cola" with "Coca Cola" would be this rule guessing at a difference it was not asked to
    /// erase - the kind of guess that merges two products into one price.
    /// </summary>
    [Fact]
    public void Punctuation_is_not_folded() =>
        Assert.NotEqual(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            PriceCatalogueWaitingKey.For("CocaCola Classic", "375mL"));

    /// <summary>
    /// The same name in a different size is a different search, which is the whole reason the unit
    /// label is part of the key.
    /// </summary>
    [Fact]
    public void A_different_unit_label_is_a_different_key() =>
        Assert.NotEqual(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "600mL"));

    /// <summary>
    /// A product with no unit label has one key, not two. <c>Product.Unit</c> is nullable, so
    /// <see langword="null"/> and <c>""</c> have to mean the same thing or one product becomes two
    /// searches.
    /// </summary>
    [Fact]
    public void A_missing_unit_label_and_an_empty_one_are_one_key() =>
        Assert.Equal(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", null),
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "   "));

    /// <summary>
    /// And "no unit label" is still distinguishable from a product whose name ran on into one. The
    /// separator is escaped rather than assumed absent, so no two different pairs can produce one
    /// key - otherwise a price collected for <c>Cola|375</c> would be recorded against <c>Cola</c> in
    /// <c>375</c>.
    /// </summary>
    [Fact]
    public void The_separator_inside_a_part_does_not_collide_with_the_separator_between_parts() =>
        Assert.NotEqual(
            PriceCatalogueWaitingKey.For("Cola|375", null),
            PriceCatalogueWaitingKey.For("Cola", "375"));

    /// <summary>
    /// The column bound is computed from the part limits rather than guessed, escaping included, so
    /// the longest search text the tracking policy accepts still fits the column that stores its key.
    /// </summary>
    [Fact]
    public void The_key_bound_covers_the_longest_search_text_the_policy_accepts()
    {
        var key = PriceCatalogueWaitingKey.For(
            new string('|', ProductPriceTrackingPolicy.MaxSearchNameLength),
            new string('|', ProductPriceTrackingPolicy.MaxUnitLabelLength));

        Assert.Equal(PriceCatalogueWaitingKey.MaxLength, key.Length);
    }

    /// <summary>
    /// The search text is captured from the product's own name and unit label, trimmed, and a unit
    /// label the product does not have is stored as the empty string so "no unit label" has one
    /// spelling.
    /// </summary>
    [Fact]
    public void The_search_text_is_the_products_own_name_and_unit_label()
    {
        Assert.Equal(
            "Coca-Cola Classic",
            ProductPriceTrackingPolicy.NormaliseSearchName("  Coca-Cola Classic "));
        Assert.Equal("375mL", ProductPriceTrackingPolicy.NormaliseUnitLabel(" 375mL "));
        Assert.Equal(string.Empty, ProductPriceTrackingPolicy.NormaliseUnitLabel(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_product_with_no_usable_name_cannot_be_tracked(string? productName)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.NormaliseSearchName(productName));

        Assert.Equal(ProductPriceTrackingPolicy.SearchNameRequiredMessage, refusal.Message);
    }

    /// <summary>
    /// A name longer than the limit is refused rather than truncated: a truncated search is a search
    /// for something else.
    /// </summary>
    [Fact]
    public void A_name_longer_than_the_limit_is_refused_rather_than_truncated()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.NormaliseSearchName(
                new string('n', ProductPriceTrackingPolicy.MaxSearchNameLength + 1)));

        Assert.Equal(ProductPriceTrackingPolicy.SearchNameTooLongMessage, refusal.Message);
    }

    [Fact]
    public void A_unit_label_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.NormaliseUnitLabel(
                new string('u', ProductPriceTrackingPolicy.MaxUnitLabelLength + 1)));

        Assert.Equal(ProductPriceTrackingPolicy.UnitLabelTooLongMessage, refusal.Message);
    }
}
