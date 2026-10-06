using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Costing;

public class StockMovementCostPolicyTests
{
    [Theory]
    [InlineData(StockAdjustmentReason.MachineRefill)]
    [InlineData(StockAdjustmentReason.Damaged)]
    [InlineData(StockAdjustmentReason.Correction)]
    [InlineData(StockAdjustmentReason.Restock)]
    public void Outgoing_movement_is_costed_at_the_current_average_when_costing_quantity_is_positive(StockAdjustmentReason reason)
    {
        var cost = StockMovementCostPolicy.Calculate(
            quantityInStock: 10, quantityChange: -3, reason, costingQuantity: 4, averageUnitCost: 1.25m, purchaseUnitCost: 9m);

        Assert.Equal(new StockMovementCost(7, 1.25m, 3.75m), cost);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Outgoing_non_restock_movement_without_positive_costing_quantity_carries_no_cost(int? costingQuantity)
    {
        var cost = StockMovementCostPolicy.Calculate(5, -2, StockAdjustmentReason.Damaged, costingQuantity, 2m, null);

        Assert.Equal(new StockMovementCost(3, null, null), cost);
    }

    [Fact]
    public void Outgoing_movement_with_negative_average_falls_back_to_the_reason_rule()
    {
        Assert.Equal(new StockMovementCost(3, null, null),
            StockMovementCostPolicy.Calculate(5, -2, StockAdjustmentReason.Expired, 4, -0.01m, 7m));
        Assert.Equal(new StockMovementCost(3, 7m, 14m),
            StockMovementCostPolicy.Calculate(5, -2, StockAdjustmentReason.Restock, 0, 2m, 7m));
    }

    [Fact]
    public void Restock_is_costed_at_its_purchase_unit_cost()
    {
        var cost = StockMovementCostPolicy.Calculate(2, 6, StockAdjustmentReason.Restock, 4, 1m, 0.333m);

        Assert.Equal(new StockMovementCost(8, 0.333m, 1.998m), cost);
    }

    [Fact]
    public void Restock_without_purchase_unit_cost_carries_no_cost()
    {
        Assert.Equal(new StockMovementCost(8, null, null),
            StockMovementCostPolicy.Calculate(2, 6, StockAdjustmentReason.Restock, 4, 1m, null));
    }

    [Theory]
    [InlineData(StockAdjustmentReason.Correction, 3)]
    [InlineData(StockAdjustmentReason.MachineRefill, 3)]
    [InlineData(StockAdjustmentReason.MachineRefill, 0)]
    public void Incoming_or_zero_non_restock_movement_carries_no_cost(StockAdjustmentReason reason, int quantityChange)
    {
        var cost = StockMovementCostPolicy.Calculate(2, quantityChange, reason, 4, 1m, 5m);

        Assert.Equal(new StockMovementCost(2 + quantityChange, null, null), cost);
    }

    [Fact]
    public void Zero_restock_movement_is_costed_at_zero_total()
    {
        Assert.Equal(new StockMovementCost(2, 5m, 0m),
            StockMovementCostPolicy.Calculate(2, 0, StockAdjustmentReason.Restock, 4, 1m, 5m));
    }

    [Fact]
    public void Movement_to_exactly_zero_stock_is_allowed()
    {
        Assert.Equal(0, StockMovementCostPolicy.Calculate(3, -3, StockAdjustmentReason.MachineRefill, 3, 1m, null).NewStockQuantity);
    }

    [Fact]
    public void Movement_that_would_make_stock_negative_is_rejected_with_the_available_stock()
    {
        var exception = Assert.Throws<InsufficientStockException>(() =>
            StockMovementCostPolicy.Calculate(2, -3, StockAdjustmentReason.MachineRefill, 2, 1m, null));

        Assert.Equal("Not enough products in stock. Available stock: 2", exception.Message);
    }

    [Fact]
    public void Movement_overflowing_stock_quantity_throws()
    {
        Assert.Throws<OverflowException>(() =>
            StockMovementCostPolicy.Calculate(int.MaxValue, 1, StockAdjustmentReason.Restock, 0, 0m, 1m));
    }
}
