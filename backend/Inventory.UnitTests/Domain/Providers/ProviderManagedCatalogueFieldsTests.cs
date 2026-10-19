using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Providers;

/// <summary>
/// The one statement of which catalogue fields a provider owns (issue #736). The field names are a
/// wire contract - a client locks exactly these inputs - so they are asserted literally, and the
/// refusals have to name the provider, because an operator who is told only "no" cannot find where
/// the value comes from.
/// </summary>
public class ProviderManagedCatalogueFieldsTests
{
    [Fact]
    public void A_provider_managed_product_locks_its_name_and_its_category()
    {
        Assert.Equal(
            new[] { "name", "categoryId" },
            ProviderManagedCatalogueFields.ForProduct(isProviderManaged: true));
    }

    [Fact]
    public void A_local_product_locks_nothing()
    {
        Assert.Empty(ProviderManagedCatalogueFields.ForProduct(isProviderManaged: false));
    }

    [Fact]
    public void A_provider_managed_category_locks_its_name_only()
    {
        Assert.Equal(new[] { "name" }, ProviderManagedCatalogueFields.ForCategory(isProviderManaged: true));
        Assert.Empty(ProviderManagedCatalogueFields.ForCategory(isProviderManaged: false));
    }

    [Fact]
    public void Each_refusal_names_the_provider_and_says_nothing_was_saved()
    {
        var refusals = new[]
        {
            ProviderManagedCatalogueFields.ProductNameRefusal(ExternalProvider.Nayax),
            ProviderManagedCatalogueFields.ProductCategoryRefusal(ExternalProvider.Nayax),
            ProviderManagedCatalogueFields.CategoryNameRefusal(ExternalProvider.Nayax),
        };

        Assert.All(refusals, refusal => Assert.Contains("Nayax", refusal, StringComparison.Ordinal));
        Assert.All(
            refusals,
            refusal => Assert.Contains("Nothing was saved.", refusal, StringComparison.Ordinal));
    }

    /// <summary>
    /// A product refusal also says what is still editable, so it does not read as "this product is
    /// read-only": the price and every other local field stay editable for a linked product.
    /// </summary>
    [Fact]
    public void A_product_refusal_says_the_price_is_still_editable()
    {
        Assert.Contains(
            "price",
            ProviderManagedCatalogueFields.ProductNameRefusal(ExternalProvider.Nayax),
            StringComparison.Ordinal);
        Assert.Contains(
            "price",
            ProviderManagedCatalogueFields.ProductCategoryRefusal(ExternalProvider.Nayax),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A row carrying an external id always belongs to a business whose provider is set, so this
    /// describes a state the application cannot reach - and it still has to produce a sentence,
    /// never a null provider name.
    /// </summary>
    [Fact]
    public void An_unknown_provider_is_described_rather_than_named()
    {
        Assert.Contains(
            "this business's sales provider",
            ProviderManagedCatalogueFields.ProductNameRefusal(null),
            StringComparison.Ordinal);
    }
}
