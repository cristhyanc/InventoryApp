using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// The invariants on the catalogue fields an operator maintains by hand (issue #736): a product has
/// a name, and since issue #895 a unit label. Both the create and the update use case apply the name
/// rule, so a product cannot be edited into a state it could not have been created in.
///
/// <para>The unit label is required asymmetrically, which is the whole point of issue #895: a create
/// refuses a blank one outright, and an update refuses one only when the <em>stored</em> label is
/// nonblank, so no request can blank a label while a legacy product that never had one can still be
/// deactivated and reactivated through the full update the Products list writes.</para>
///
/// <para>The selling-price bound this policy also carried is gone with the field itself (issue
/// #792, A2 of #786): a product carries no price, so there is none to validate.</para>
/// </summary>
public class ProductCataloguePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_name_is_refused(string? name) =>
        Assert.Equal(ProductCataloguePolicy.NameRequiredMessage, ProductCataloguePolicy.Validate(name));

    [Fact]
    public void A_named_product_is_valid() => Assert.Null(ProductCataloguePolicy.Validate("Coke"));

    /// <summary>
    /// Surrounding whitespace does not make a name missing: the refusal is about a name nobody
    /// typed, and a padded one is a name.
    /// </summary>
    [Fact]
    public void A_padded_name_is_valid() => Assert.Null(ProductCataloguePolicy.Validate("  Coke  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_unit_label_is_refused_on_a_create(string? unit) =>
        Assert.Equal(ProductCataloguePolicy.UnitRequiredMessage, ProductCataloguePolicy.ValidateUnit(unit));

    [Theory]
    [InlineData("can")]
    [InlineData("  can  ")]
    public void A_submitted_unit_label_is_valid_on_a_create(string unit) =>
        Assert.Null(ProductCataloguePolicy.ValidateUnit(unit));

    /// <summary>
    /// No request may blank a label a product already has - neither by sending <c>null</c> nor by
    /// sending whitespace.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blanking_a_stored_unit_label_is_refused(string? submitted) =>
        Assert.Equal(
            ProductCataloguePolicy.UnitRequiredMessage,
            ProductCataloguePolicy.UnitChangeRefusal("bag", submitted));

    /// <summary>
    /// The legacy row the asymmetry exists for: a product whose stored label is missing keeps being
    /// updatable with no label, which is what the Products list's Deactivate and Reactivate send.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "   ")]
    [InlineData("   ", null)]
    [InlineData("", "")]
    public void A_blank_stored_unit_label_may_stay_blank(string? stored, string? submitted) =>
        Assert.Null(ProductCataloguePolicy.UnitChangeRefusal(stored, submitted));

    [Theory]
    [InlineData(null)]
    [InlineData("bag")]
    public void A_nonblank_submitted_unit_label_is_always_accepted(string? stored) =>
        Assert.Null(ProductCataloguePolicy.UnitChangeRefusal(stored, "can"));

    /// <summary>
    /// What is stored: trailing space is not a different unit, and a blank label is no label at all
    /// rather than a column full of spaces.
    /// </summary>
    [Theory]
    [InlineData("can", "can")]
    [InlineData("  can  ", "can")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void A_unit_label_is_normalized_before_it_is_stored(string? submitted, string? expected) =>
        Assert.Equal(expected, ProductCataloguePolicy.NormalizeUnit(submitted));
}
