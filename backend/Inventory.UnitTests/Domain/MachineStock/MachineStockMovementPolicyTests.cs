using Inventory.Domain.MachineStock;
using Xunit;

namespace InventoryApi.Tests.Domain.MachineStock;

/// <summary>
/// The machine-stock movement semantics (issue #56): what each persisted fact means for the units in
/// the machine, and which kinds consume business-owned costing inventory. The costing assertions are
/// the regression guard for AGENTS.md § Inventory and historical costing invariants - a refill or a
/// return is an internal transfer and a count is an observation, so only a completed sale may ever
/// consume costing inventory.
/// </summary>
public class MachineStockMovementPolicyTests
{
    private static readonly DateTime At = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [Fact]
    public void A_negative_storage_movement_is_a_refill_into_the_machine()
    {
        var movement = MachineStockMovementPolicy.FromRefill(new MachineRefillFact(11, 500, -12, At));

        Assert.Equal(MachineStockMovementKind.Refill, movement.Kind);
        Assert.Equal(12, movement.MachineQuantityChange);
        Assert.Equal(500, movement.ProductId);
        Assert.Equal(At, movement.EffectiveAtUtc);
        Assert.Equal(11, movement.SourceId);
        Assert.Null(movement.CountedQuantity);
    }

    [Fact]
    public void A_positive_storage_movement_is_a_return_out_of_the_machine()
    {
        var movement = MachineStockMovementPolicy.FromRefill(new MachineRefillFact(12, 500, 4, At));

        Assert.Equal(MachineStockMovementKind.ReturnToStorage, movement.Kind);
        Assert.Equal(-4, movement.MachineQuantityChange);
    }

    [Fact]
    public void A_zero_quantity_refill_stays_a_refill_of_nothing()
    {
        var movement = MachineStockMovementPolicy.FromRefill(new MachineRefillFact(13, 500, 0, At));

        Assert.Equal(MachineStockMovementKind.Refill, movement.Kind);
        Assert.Equal(0, movement.MachineQuantityChange);
    }

    [Fact]
    public void One_completed_sale_dispenses_exactly_one_unit()
    {
        var movement = MachineStockMovementPolicy.FromSale(new MachineSaleFact(9001, 500, At));

        Assert.Equal(MachineStockMovementKind.CompletedSale, movement.Kind);
        Assert.Equal(-1, movement.MachineQuantityChange);
        Assert.Equal(9001, movement.SourceId);
    }

    [Fact]
    public void A_count_carries_the_level_it_observed_and_no_change()
    {
        var movement = MachineStockMovementPolicy.FromCount(new MachineStockCountFact(7, 500, 23, At));

        Assert.Equal(MachineStockMovementKind.CountedReconciliation, movement.Kind);
        Assert.Equal(0, movement.MachineQuantityChange);
        Assert.Equal(23, movement.CountedQuantity);
    }

    [Theory]
    [InlineData(MachineStockMovementKind.Refill, false)]
    [InlineData(MachineStockMovementKind.ReturnToStorage, false)]
    [InlineData(MachineStockMovementKind.CountedReconciliation, false)]
    [InlineData(MachineStockMovementKind.CompletedSale, true)]
    public void Only_a_completed_sale_consumes_business_owned_costing_inventory(
        MachineStockMovementKind kind, bool consumes) =>
        Assert.Equal(consumes, MachineStockMovementPolicy.ConsumesBusinessCostingInventory(kind));
}
