using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

public class ProductInitialCostPolicyTests
{
    [Fact]
    public void Validate_AcceptsNullCost()
    {
        Assert.Null(ProductInitialCostPolicy.Validate(null));
    }

    [Fact]
    public void Validate_AcceptsZeroOrPositiveCost()
    {
        Assert.Null(ProductInitialCostPolicy.Validate(0m));
        Assert.Null(ProductInitialCostPolicy.Validate(4.5m));
    }

    [Fact]
    public void Validate_RejectsNegativeCost()
    {
        Assert.Equal(
            ProductInitialCostPolicy.NegativeInitialUnitCostMessage,
            ProductInitialCostPolicy.Validate(-0.01m));
    }
}
