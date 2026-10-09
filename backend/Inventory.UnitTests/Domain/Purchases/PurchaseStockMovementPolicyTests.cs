using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

public class PurchaseStockMovementPolicyTests
{
    [Fact]
    public void ToStockQuantity_truncates_a_whole_decimal_to_an_int()
    {
        Assert.Equal(24, PurchaseStockMovementPolicy.ToStockQuantity(24m));
    }

    [Fact]
    public void ToStockQuantity_overflows_for_a_quantity_outside_int_range()
    {
        Assert.Throws<OverflowException>(() => PurchaseStockMovementPolicy.ToStockQuantity(int.MaxValue + 1m));
    }

    [Fact]
    public void TotalCost_multiplies_quantity_by_unit_cost()
    {
        Assert.Equal(36m, PurchaseStockMovementPolicy.TotalCost(24m, 1.5m));
    }
}
