using Inventory.Domain.Categories;
using Inventory.Domain.Providers;
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

    /// <summary>
    /// A category the business's sales provider names is refused with a message naming the
    /// provider (issue #1026), the same wording rule <c>MachineCataloguePolicy.ProviderMachineDeletionRefusal</c>
    /// follows for a machine.
    /// </summary>
    [Fact]
    public void A_provider_named_categorys_deletion_refusal_names_the_provider() =>
        Assert.Equal(
            "This category comes from Nayax and cannot be deleted here. Nothing was deleted.",
            CategoryCataloguePolicy.ProviderCategoryDeletionRefusal(ExternalProvider.Nayax));

    /// <summary>
    /// A row carrying a provider id always belongs to a business whose <c>SalesProvider</c> is set,
    /// so the fallback describes a state this application cannot reach rather than guessing a name.
    /// </summary>
    [Fact]
    public void A_provider_named_categorys_deletion_refusal_falls_back_with_no_named_provider() =>
        Assert.Contains(
            "this business's sales provider",
            CategoryCataloguePolicy.ProviderCategoryDeletionRefusal(null),
            StringComparison.Ordinal);
}
