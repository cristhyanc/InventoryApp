using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// The invariant on the catalogue fields an operator maintains by hand (issue #736): a product has
/// a name. Both the create and the update use case apply it, so a product cannot be edited into a
/// state it could not have been created in.
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
}
