using Inventory.Application.Costing;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// <see cref="RecordInventoryMovement"/> (issue #296): the Application movement-recording use case
/// that replaced <c>InventoryApi.Services.InventoryCostService.ApplyMovement</c>. These tests run it
/// against an in-memory port so they prove the use case itself - not an EF adapter - owns the
/// movement-cost decision, and that a rejected movement stages nothing.
/// </summary>
public class RecordInventoryMovementTests
{
    private const long ProductId = 7;

    [Fact]
    public async Task Positive_restock_is_costed_at_the_purchase_unit_cost()
    {
        var store = new FakeMovementStore(new InventoryMovementCostState(QuantityInStock: 4, CostingQuantity: 4, AverageUnitCost: 2m));

        var staged = await new RecordInventoryMovement(store)
            .RecordAsync(new InventoryMovement(ProductId, 6, StockAdjustmentReason.Restock, "Restock", PurchaseUnitCost: 3.5m), CancellationToken.None);

        var movement = Assert.Single(store.Staged);
        Assert.Equal(10, movement.QuantityAfter);
        Assert.Equal(10, staged.QuantityAfter);
        Assert.Equal(3.5m, movement.UnitCost);
        Assert.Equal(21m, movement.TotalCost);
        Assert.Equal(StockAdjustmentReason.Restock, movement.Reason);
    }

    [Fact]
    public async Task Negative_correction_is_costed_at_the_current_average_without_rounding()
    {
        var store = new FakeMovementStore(new InventoryMovementCostState(QuantityInStock: 3, CostingQuantity: 3, AverageUnitCost: 10m / 3m));

        await new RecordInventoryMovement(store)
            .RecordAsync(new InventoryMovement(ProductId, -2, StockAdjustmentReason.Correction, "Count"), CancellationToken.None);

        var movement = Assert.Single(store.Staged);
        Assert.Equal(1, movement.QuantityAfter);
        Assert.Equal(10m / 3m, movement.UnitCost);
        Assert.Equal(10m / 3m * 2, movement.TotalCost);
    }

    [Fact]
    public async Task Machine_refill_reduces_physical_stock_and_carries_its_audit_attributes()
    {
        var store = new FakeMovementStore(new InventoryMovementCostState(QuantityInStock: 12, CostingQuantity: 12, AverageUnitCost: 2m));
        var eatBefore = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        await new RecordInventoryMovement(store).RecordAsync(
            new InventoryMovement(
                ProductId, -5, StockAdjustmentReason.MachineRefill, "Nayax Sync Restock",
                MachineId: 42, EatBefore: eatBefore, Source: StockAdjustmentSource.Nayax),
            CancellationToken.None);

        var movement = Assert.Single(store.Staged);
        Assert.Equal(7, movement.QuantityAfter);
        Assert.Equal(StockAdjustmentReason.MachineRefill, movement.Reason);
        Assert.Equal(StockAdjustmentSource.Nayax, movement.Source);
        Assert.Equal(42, movement.MachineId);
        Assert.Equal(eatBefore, movement.EatBefore);
        Assert.Equal("Nayax Sync Restock", movement.Notes);
    }

    [Fact]
    public async Task Insufficient_stock_throws_and_stages_nothing()
    {
        var store = new FakeMovementStore(new InventoryMovementCostState(QuantityInStock: 2, CostingQuantity: 2, AverageUnitCost: 1m));

        var exception = await Assert.ThrowsAsync<InsufficientStockException>(() => new RecordInventoryMovement(store)
            .RecordAsync(new InventoryMovement(ProductId, -3, StockAdjustmentReason.Damaged, "Broken"), CancellationToken.None));

        Assert.Equal("Not enough products in stock. Available stock: 2", exception.Message);
        Assert.Empty(store.Staged);
    }

    [Fact]
    public async Task Negative_purchase_cost_is_rejected_and_stages_nothing()
    {
        var store = new FakeMovementStore(new InventoryMovementCostState(QuantityInStock: 0, CostingQuantity: 0, AverageUnitCost: 0m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecordInventoryMovement(store)
            .RecordAsync(new InventoryMovement(ProductId, 5, StockAdjustmentReason.Restock, "Restock", PurchaseUnitCost: -1m), CancellationToken.None));

        Assert.Empty(store.Staged);
    }

    [Fact]
    public async Task Unknown_product_is_rejected_and_stages_nothing()
    {
        var store = new FakeMovementStore(state: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecordInventoryMovement(store)
            .RecordAsync(new InventoryMovement(ProductId, 1, StockAdjustmentReason.Restock, "Restock", PurchaseUnitCost: 1m), CancellationToken.None));

        Assert.Empty(store.Staged);
    }

    private sealed class FakeMovementStore(InventoryMovementCostState? state) : IInventoryMovementStore
    {
        public List<CostedInventoryMovement> Staged { get; } = [];

        public Task<InventoryMovementCostState?> GetCostStateAsync(long productId, CancellationToken cancellationToken) =>
            Task.FromResult(productId == ProductId ? state : null);

        public IStagedInventoryMovement Stage(CostedInventoryMovement movement)
        {
            Staged.Add(movement);
            return new StagedMovement(Staged.Count, movement.QuantityAfter);
        }

        private sealed record StagedMovement(int Id, int QuantityAfter) : IStagedInventoryMovement;
    }
}
