using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

public class ProductRestockPolicyTests
{
    [Fact]
    public void Validate_AcceptsRestockToAtOrAboveThreshold()
    {
        Assert.Null(ProductRestockPolicy.Validate(lowStockThreshold: 10, restockTo: 10));
        Assert.Null(ProductRestockPolicy.Validate(lowStockThreshold: 10, restockTo: 20));
    }

    [Fact]
    public void Validate_RejectsNegativeLowStockThreshold()
    {
        Assert.Equal(
            ProductRestockPolicy.NegativeLowStockThresholdMessage,
            ProductRestockPolicy.Validate(lowStockThreshold: -1, restockTo: 0));
    }

    [Fact]
    public void Validate_RejectsNegativeRestockTo()
    {
        Assert.Equal(
            ProductRestockPolicy.NegativeRestockToMessage,
            ProductRestockPolicy.Validate(lowStockThreshold: 0, restockTo: -1));
    }

    [Fact]
    public void Validate_RejectsRestockToBelowThreshold()
    {
        Assert.Equal(
            ProductRestockPolicy.RestockToBelowThresholdMessage,
            ProductRestockPolicy.Validate(lowStockThreshold: 10, restockTo: 9));
    }
}
