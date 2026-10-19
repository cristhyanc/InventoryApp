using Inventory.Domain.Products;
using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// Whether a product may be deleted (issue #736, parent #329 decision P2). The refusal has to say
/// what is in the way and how a product is taken out of use instead, because the alternative - a
/// cascade that erases stock history, or a database error - is exactly what this policy exists to
/// prevent (AGENTS.md § Inventory and historical costing invariants).
/// </summary>
public class ProductDeletionPolicyTests
{
    [Fact]
    public void A_product_nothing_refers_to_may_be_deleted()
    {
        Assert.Null(ProductDeletionPolicy.Refusal(
            isProviderManaged: false, provider: null, ProductDeletionReferences.None));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0, 0, "1 stock movement")]
    [InlineData(3, 0, 0, 0, 0, 0, "3 stock movements")]
    [InlineData(0, 2, 0, 0, 0, 0, "2 purchase lines")]
    [InlineData(0, 0, 1, 0, 0, 0, "1 supplier order line")]
    [InlineData(0, 0, 0, 4, 0, 0, "4 sales")]
    [InlineData(0, 0, 0, 0, 1, 0, "1 machine stock count")]
    [InlineData(0, 0, 0, 0, 0, 2, "2 costing records")]
    public void Each_kind_of_history_refuses_the_delete_and_is_named_with_its_count(
        int stockAdjustments,
        int purchaseLines,
        int supplierOrderLines,
        int sales,
        int machineCounts,
        int costingRecords,
        string expected)
    {
        var refusal = ProductDeletionPolicy.Refusal(
            isProviderManaged: false,
            provider: null,
            new ProductDeletionReferences(
                stockAdjustments, purchaseLines, supplierOrderLines, sales, machineCounts, costingRecords));

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Contains("Deactivate it instead", refusal, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted.", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_kinds_of_history_are_all_named()
    {
        var refusal = ProductDeletionPolicy.Refusal(
            isProviderManaged: false,
            provider: null,
            new ProductDeletionReferences(2, 1, 0, 3, 0, 0));

        Assert.Equal(
            "This product cannot be deleted because its history refers to it (2 stock movements, "
                + "1 purchase line, 3 sales). Deactivate it instead - a deactivated product keeps "
                + "its history, stays out of the reorder alerts and can be reactivated. Nothing was "
                + "deleted.",
            refusal);
    }

    /// <summary>
    /// A product the provider names is refused even with no history at all, and for its own reason:
    /// the next catalogue import would simply create it again.
    /// </summary>
    [Fact]
    public void A_provider_managed_product_may_not_be_deleted_even_with_no_history()
    {
        var refusal = ProductDeletionPolicy.Refusal(
            isProviderManaged: true, ExternalProvider.Nayax, ProductDeletionReferences.None);

        Assert.NotNull(refusal);
        Assert.Contains("managed by Nayax", refusal, StringComparison.Ordinal);
        Assert.Contains("Deactivate it instead", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void References_report_whether_anything_refers_to_the_product()
    {
        Assert.False(ProductDeletionReferences.None.Any);
        Assert.True(new ProductDeletionReferences(0, 0, 0, 0, 0, 1).Any);
    }
}
