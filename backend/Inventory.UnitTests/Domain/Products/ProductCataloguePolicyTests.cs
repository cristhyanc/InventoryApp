using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// The two invariants on the catalogue fields an operator maintains by hand (issue #736): a product
/// has a name, and its selling price is not negative. Both the create and the update use case apply
/// them, so a product cannot be edited into a state it could not have been created in.
/// </summary>
public class ProductCataloguePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_name_is_refused(string? name) =>
        Assert.Equal(ProductCataloguePolicy.NameRequiredMessage, ProductCataloguePolicy.Validate(name, 1m));

    [Fact]
    public void A_negative_unit_price_is_refused() =>
        Assert.Equal(
            ProductCataloguePolicy.NegativeUnitPriceMessage,
            ProductCataloguePolicy.Validate("Coke", -0.01m));

    /// <summary>
    /// Zero is a price, not a missing one: products imported before the confirmed Nayax retail-price
    /// mapping carry it (docs/architecture.md § Product selling price), and a product given away
    /// free is a legitimate catalogue entry.
    /// </summary>
    [Fact]
    public void A_zero_unit_price_is_valid() => Assert.Null(ProductCataloguePolicy.Validate("Coke", 0m));

    [Fact]
    public void A_named_product_with_a_positive_price_is_valid() =>
        Assert.Null(ProductCataloguePolicy.Validate("Coke", 3.50m));

    /// <summary>
    /// The name is checked before the price, so a form with neither reports the field a person fills
    /// in first.
    /// </summary>
    [Fact]
    public void The_name_is_reported_before_the_price() =>
        Assert.Equal(ProductCataloguePolicy.NameRequiredMessage, ProductCataloguePolicy.Validate(null, -1m));
}
