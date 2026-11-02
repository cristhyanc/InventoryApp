using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The one formatter that turns a shared master product into the two values a business's own product
/// form is filled in with (issue #1049, parent #817): the product Name, and the unit label beside it.
///
/// <para>Two of these facts are the issue's own criteria and they are the reason this rule is in the
/// Domain rather than in a controller or an Angular component. <strong>Every label it produces parses
/// back to the master product's own size</strong> with
/// <see cref="AutomaticProductMatchPolicy.TryParseSize"/> - the rule the automatic matcher applies -
/// so a product created from a prefill can be matched to the very master product it was prefilled
/// from. And <strong>every name and label it produces is searchable</strong> by
/// <see cref="ProductPriceTrackingPolicy.TrackingRefusal"/>, so turning "Track supplier prices" on
/// for such a product is never refused by the text this formatter chose.</para>
/// </summary>
public class MasterProductPrefillTests
{
    /// <summary>The issue's worked example, in both halves.</summary>
    [Fact]
    public void The_issues_example_produces_its_stated_name_and_unit_label()
    {
        var prefill = MasterProductPrefill.For(
            "CC's",
            "Tasty Cheese Corn Chips",
            45m,
            MasterProductSizeUnit.Gram);

        Assert.Equal("CC's Tasty Cheese Corn Chips", prefill.ProductName);
        Assert.Equal("45g", prefill.UnitLabel);
    }

    /// <summary>
    /// The name is the brand and the name with single spaces: a stored value that holds a run of
    /// whitespace, or whitespace at either end, must not produce a double space a person would have
    /// to delete, and must not fold to a different search text than the same product typed by hand.
    /// </summary>
    [Theory]
    [InlineData("Coca-Cola", "Classic", "Coca-Cola Classic")]
    [InlineData("  Coca-Cola  ", "  Classic  ", "Coca-Cola Classic")]
    [InlineData("Coca  Cola", "Classic\tNo Sugar", "Coca Cola Classic No Sugar")]
    public void The_product_name_is_the_brand_and_the_name_with_single_spaces(
        string brand,
        string name,
        string expected) =>
        Assert.Equal(expected, MasterProductPrefill.ProductName(brand, name));

    /// <summary>
    /// The unit label is the size value with trailing zeros dropped and then the unit's own code,
    /// with no space - the four spellings the issue states, and the equal decimals that must not
    /// produce three different labels.
    /// </summary>
    [Theory]
    [InlineData(45, MasterProductSizeUnit.Gram, "45g")]
    [InlineData(375, MasterProductSizeUnit.Millilitre, "375ml")]
    [InlineData(1.25, MasterProductSizeUnit.Litre, "1.25l")]
    [InlineData(1, MasterProductSizeUnit.Each, "1each")]
    [InlineData(0.5, MasterProductSizeUnit.Kilogram, "0.5kg")]
    [InlineData(0.125, MasterProductSizeUnit.Kilogram, "0.125kg")]
    public void The_unit_label_is_the_size_and_then_the_unit_code(
        decimal sizeValue,
        MasterProductSizeUnit sizeUnit,
        string expected) =>
        Assert.Equal(expected, MasterProductPrefill.UnitLabel(sizeValue, sizeUnit));

    /// <summary>
    /// The trailing zeros the <em>stored scale</em> can hold are dropped, which an inline decimal
    /// cannot express: a column of scale 3 holds <c>375.000</c>, and <c>"375.000ml"</c> is not the
    /// label a person writes or the one the matcher's unit words are compared against.
    /// </summary>
    [Fact]
    public void A_stored_size_with_trailing_zeros_produces_the_label_without_them()
    {
        Assert.Equal("375ml", MasterProductPrefill.UnitLabel(375.000m, MasterProductSizeUnit.Millilitre));
        Assert.Equal("1.25l", MasterProductPrefill.UnitLabel(1.250m, MasterProductSizeUnit.Litre));
        Assert.Equal("45g", MasterProductPrefill.UnitLabel(45.00m, MasterProductSizeUnit.Gram));
        Assert.Equal("1each", MasterProductPrefill.UnitLabel(1.000m, MasterProductSizeUnit.Each));
    }

    /// <summary>
    /// The issue's criterion: <strong>every</strong> label this formatter produces parses back to the
    /// master product's own size, in the unit the matcher compares in. A label that did not would
    /// prefill a product the automatic matcher could never match to the master product it came from.
    /// </summary>
    [Theory]
    [InlineData(45, MasterProductSizeUnit.Gram)]
    [InlineData(375, MasterProductSizeUnit.Millilitre)]
    [InlineData(1.25, MasterProductSizeUnit.Litre)]
    [InlineData(1, MasterProductSizeUnit.Each)]
    [InlineData(6, MasterProductSizeUnit.Each)]
    [InlineData(0.5, MasterProductSizeUnit.Kilogram)]
    [InlineData(2.5, MasterProductSizeUnit.Kilogram)]
    [InlineData(0.125, MasterProductSizeUnit.Litre)]
    [InlineData(1000, MasterProductSizeUnit.Millilitre)]
    public void Every_unit_label_parses_back_to_the_master_products_own_size(
        decimal sizeValue,
        MasterProductSizeUnit sizeUnit)
    {
        var label = MasterProductPrefill.UnitLabel(sizeValue, sizeUnit);

        Assert.True(
            AutomaticProductMatchPolicy.TryParseSize(label, out var parsed),
            $"\"{label}\" is a unit label this formatter produced and the matcher cannot read it.");

        var own = AutomaticProductMatchPolicy.SizeOf(sizeValue, sizeUnit);
        Assert.Equal(own.Dimension, parsed!.Dimension);
        Assert.Equal(own.CanonicalValue, parsed.CanonicalValue);
    }

    /// <summary>
    /// And the same for every declared unit at once, so a unit added to the vocabulary later is
    /// covered by this criterion on the day it is declared rather than when somebody remembers to add
    /// a row above.
    /// </summary>
    [Fact]
    public void Every_declared_unit_produces_a_label_the_matcher_reads_as_that_size()
    {
        foreach (var unit in Enum.GetValues<MasterProductSizeUnit>())
        {
            var label = MasterProductPrefill.UnitLabel(2m, unit);

            Assert.True(
                AutomaticProductMatchPolicy.TryParseSize(label, out var parsed),
                $"\"{label}\" is a unit label this formatter produced and the matcher cannot read it.");

            var own = AutomaticProductMatchPolicy.SizeOf(2m, unit);
            Assert.Equal(own.Dimension, parsed!.Dimension);
            Assert.Equal(own.CanonicalValue, parsed.CanonicalValue);
        }
    }

    /// <summary>
    /// The issue's other criterion: the prefilled name and label pass the tracking switch's own
    /// searchability rules, so a product created from a prefill can have supplier prices tracked
    /// without being edited first. The longest description this catalogue can hold is included,
    /// because that is the only one whose combined name could exceed the search-text limit.
    /// </summary>
    [Theory]
    [InlineData("CC's", "Tasty Cheese Corn Chips", 45, MasterProductSizeUnit.Gram)]
    [InlineData("Coca-Cola", "Classic", 375, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", 1.25, MasterProductSizeUnit.Litre)]
    [InlineData("Allen's", "Party Mix", 1, MasterProductSizeUnit.Each)]
    public void A_prefill_passes_the_tracking_switchs_searchability_rules(
        string brand,
        string name,
        decimal sizeValue,
        MasterProductSizeUnit sizeUnit)
    {
        var prefill = MasterProductPrefill.For(brand, name, sizeValue, sizeUnit);

        Assert.Null(ProductPriceTrackingPolicy.TrackingRefusal(
            prefill.ProductName,
            prefill.UnitLabel));
    }

    /// <summary>
    /// The longest description the catalogue stores still produces a trackable prefill: a brand at
    /// <c>MaxBrandLength</c> and a name at <c>MaxNameLength</c> are 301 characters together, well
    /// inside <c>ProductPriceTrackingPolicy.MaxSearchNameLength</c> - which is what makes "a prefill
    /// is always searchable" true for every row rather than for the short ones.
    /// </summary>
    [Fact]
    public void The_longest_stored_description_still_prefills_a_trackable_product()
    {
        var prefill = MasterProductPrefill.For(
            new string('b', MasterProductPolicy.MaxBrandLength),
            new string('n', MasterProductPolicy.MaxNameLength),
            375m,
            MasterProductSizeUnit.Millilitre);

        Assert.Null(ProductPriceTrackingPolicy.TrackingRefusal(
            prefill.ProductName,
            prefill.UnitLabel));
        Assert.True(
            prefill.ProductName.Length <= ProductPriceTrackingPolicy.MaxSearchNameLength,
            "A prefilled product name must fit the search text the tracking switch captures.");
    }

    /// <summary>
    /// A unit no declared code names is refused rather than formatted into a label nobody could read
    /// back - the vocabulary's own rule, not a second one: a persisted row carries an integer, and a
    /// value outside the declared list is not a size this application can state.
    /// </summary>
    [Fact]
    public void A_unit_outside_the_declared_vocabulary_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => MasterProductPrefill.UnitLabel(375m, (MasterProductSizeUnit)99));
}
