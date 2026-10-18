using Inventory.Domain.Categories;
using Xunit;

namespace InventoryApi.Tests.Domain.Categories;

/// <summary>
/// The invariants on a category an operator maintains by hand (issue #736): it has a name, the name
/// is compared trimmed, and a category products still use is not deleted - with the count in the
/// message, because that is what the operator has to act on.
/// </summary>
public class CategoryCataloguePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_name_is_refused(string? name) =>
        Assert.Equal(CategoryCataloguePolicy.NameRequiredMessage, CategoryCataloguePolicy.Validate(name));

    [Fact]
    public void A_named_category_is_valid() => Assert.Null(CategoryCataloguePolicy.Validate("Drinks"));

    [Fact]
    public void A_name_is_stored_trimmed() =>
        Assert.Equal("Drinks", CategoryCataloguePolicy.Normalize("  Drinks  "));

    [Fact]
    public void A_duplicate_name_refusal_quotes_the_name_and_saves_nothing()
    {
        var message = CategoryCataloguePolicy.DuplicateNameMessage("Drinks");

        Assert.Contains("\"Drinks\"", message, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_no_product_uses_may_be_deleted() =>
        Assert.Null(CategoryCataloguePolicy.DeletionRefusal(0));

    [Theory]
    [InlineData(1, "1 product uses")]
    [InlineData(4, "4 products use")]
    public void A_category_products_use_is_refused_with_the_count(int products, string expected)
    {
        var refusal = CategoryCataloguePolicy.DeletionRefusal(products);

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted.", refusal, StringComparison.Ordinal);
    }
}
