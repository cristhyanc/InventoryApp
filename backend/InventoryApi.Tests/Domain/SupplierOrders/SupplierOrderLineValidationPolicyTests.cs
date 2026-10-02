using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Domain.SupplierOrders;

public class SupplierOrderLineValidationPolicyTests
{
    [Fact]
    public void No_lines_is_invalid()
    {
        Assert.True(SupplierOrderLineValidationPolicy.HasInvalidQuantity([]));
    }

    [Fact]
    public void A_positive_whole_unit_quantity_is_valid()
    {
        Assert.False(SupplierOrderLineValidationPolicy.HasInvalidQuantity([new SupplierOrderLineCandidate(1, 5m)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void A_non_positive_or_fractional_quantity_is_invalid(double quantity)
    {
        Assert.True(SupplierOrderLineValidationPolicy.HasInvalidQuantity([new SupplierOrderLineCandidate(1, (decimal)quantity)]));
    }

    [Fact]
    public void Distinct_products_have_no_duplicate()
    {
        Assert.False(SupplierOrderLineValidationPolicy.HasDuplicateProduct(
        [
            new SupplierOrderLineCandidate(1, 5m),
            new SupplierOrderLineCandidate(2, 5m),
        ]));
    }

    [Fact]
    public void A_repeated_product_is_a_duplicate()
    {
        Assert.True(SupplierOrderLineValidationPolicy.HasDuplicateProduct(
        [
            new SupplierOrderLineCandidate(1, 5m),
            new SupplierOrderLineCandidate(1, 3m),
        ]));
    }
}
