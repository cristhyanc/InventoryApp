using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// The one way a product is named in a caller-facing message (issue #833). An operator reading a
/// refused purchase, import or costing repair has to know which product to fix without looking the
/// ID up, so the name is shown with the ID; a product whose name was never loaded, or is blank,
/// keeps the bare-ID wording these messages have always used rather than showing empty brackets.
/// </summary>
public class ProductLabelTests
{
    [Fact]
    public void A_named_product_is_shown_as_its_name_and_id()
    {
        Assert.Equal("Product Coke (12)", ProductLabel.Sentence(12, "Coke"));
        Assert.Equal("product Coke (12)", ProductLabel.Phrase(12, "Coke"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_missing_or_blank_name_falls_back_to_the_bare_id(string? name)
    {
        Assert.Equal("Product 12", ProductLabel.Sentence(12, name));
        Assert.Equal("product 12", ProductLabel.Phrase(12, name));
    }

    [Fact]
    public void The_id_is_rendered_invariantly_whatever_its_magnitude()
    {
        Assert.Equal("Product Coke (1234567)", ProductLabel.Sentence(1234567, "Coke"));
        Assert.Equal("product 9007199254740993", ProductLabel.Phrase(9007199254740993, null));
    }
}
