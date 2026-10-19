using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Providers;

/// <summary>
/// Whether a provider's catalogue may be imported into a business that maintains its own
/// (issue #736, parent #329 decision P2). The import identifies a product only by the provider's own
/// id, so for a business holding products with no such id it would create a second row for each of
/// them - which is why the import is refused until the catalogue review ships (issues #738/#739).
/// </summary>
public class ManualCatalogueImportPolicyTests
{
    /// <summary>
    /// A business whose catalogue the provider manages entirely imports exactly as before: the
    /// refusal is about the rows the import cannot recognise, not about the import.
    /// </summary>
    [Fact]
    public void A_business_whose_products_all_carry_a_provider_id_may_import() =>
        Assert.Null(ManualCatalogueImportPolicy.Refusal(0));

    [Theory]
    [InlineData(1, "1 product")]
    [InlineData(7, "7 products")]
    public void A_business_with_manual_products_is_refused_with_the_count(int products, string expected)
    {
        var refusal = ManualCatalogueImportPolicy.Refusal(products);

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Contains("duplicate", refusal, StringComparison.Ordinal);
        Assert.Contains("Nothing was imported.", refusal, StringComparison.Ordinal);
    }
}
