using Inventory.Domain.Exceptions;
using Inventory.Domain.InventoryCounting;
using Xunit;

namespace InventoryApi.Tests.Domain.InventoryCounting;

public class InventoryCountAdjustmentPolicyTests
{
    [Fact]
    public void Counted_greater_than_current_resolves_to_a_positive_increase()
    {
        var plan = InventoryCountAdjustmentPolicy.Resolve(currentStock: 17, countedStock: 22);

        Assert.Equal(InventoryCountMovementKind.Increase, plan.Kind);
        Assert.Equal(5, plan.QuantityChange);
    }

    [Fact]
    public void Counted_less_than_current_resolves_to_a_negative_decrease()
    {
        var plan = InventoryCountAdjustmentPolicy.Resolve(currentStock: 32, countedStock: 28);

        Assert.Equal(InventoryCountMovementKind.Decrease, plan.Kind);
        Assert.Equal(-4, plan.QuantityChange);
    }

    [Fact]
    public void Counted_equal_to_current_resolves_to_no_movement()
    {
        var plan = InventoryCountAdjustmentPolicy.Resolve(currentStock: 46, countedStock: 46);

        Assert.Equal(InventoryCountMovementKind.None, plan.Kind);
        Assert.Equal(0, plan.QuantityChange);
    }

    [Fact]
    public void Negative_counted_stock_is_rejected()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => InventoryCountAdjustmentPolicy.Resolve(currentStock: 5, countedStock: -1));

        Assert.Equal("Counted stock cannot be negative.", exception.Message);
    }

    [Fact]
    public void Counted_stock_of_zero_against_positive_current_resolves_to_a_full_decrease()
    {
        var plan = InventoryCountAdjustmentPolicy.Resolve(currentStock: 3, countedStock: 0);

        Assert.Equal(InventoryCountMovementKind.Decrease, plan.Kind);
        Assert.Equal(-3, plan.QuantityChange);
    }
}
