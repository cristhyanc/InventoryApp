using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

public class ProductInitialCostPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_AcceptsNoCost_WhenThereIsNoOpeningStock(int openingStock)
    {
        Assert.Null(ProductInitialCostPolicy.Validate(openingStock, null));
    }

    [Fact]
    public void Validate_AcceptsZeroOrPositiveCost()
    {
        Assert.Null(ProductInitialCostPolicy.Validate(0, 0m));
        Assert.Null(ProductInitialCostPolicy.Validate(0, 4.5m));
        Assert.Null(ProductInitialCostPolicy.Validate(7, 4.5m));
    }

    [Fact]
    public void Validate_RejectsNegativeCost()
    {
        Assert.Equal(
            ProductInitialCostPolicy.NegativeInitialUnitCostMessage,
            ProductInitialCostPolicy.Validate(0, -0.01m));
    }

    /// <summary>
    /// Issue #1238: opening stock above zero with no cost used to be accepted, and the uncosted
    /// opening <c>Restock</c> movement it left made every later stock write of that product fail its
    /// cost rebuild. The cost is required from the create instead.
    /// </summary>
    [Fact]
    public void Validate_RejectsAMissingCost_WhenThereIsOpeningStock()
    {
        Assert.Equal(
            ProductInitialCostPolicy.InitialUnitCostRequiredMessage,
            ProductInitialCostPolicy.Validate(1, null));
    }

    /// <summary>
    /// Free opening stock is a real answer, so zero is accepted: what the replay cannot work with is
    /// no answer at all.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAZeroCost_WhenThereIsOpeningStock()
    {
        Assert.Null(ProductInitialCostPolicy.Validate(12, 0m));
    }

    /// <summary>
    /// A negative cost is still refused with its own message, whatever the opening stock is: the
    /// new rule is about a cost nobody gave, not about one that is out of bounds.
    /// </summary>
    [Fact]
    public void Validate_RejectsANegativeCost_EvenWithOpeningStock()
    {
        Assert.Equal(
            ProductInitialCostPolicy.NegativeInitialUnitCostMessage,
            ProductInitialCostPolicy.Validate(5, -1m));
    }
}
