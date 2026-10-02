using Inventory.Domain.Costing;

namespace Inventory.Application.Costing;

/// <summary>
/// Records one stock movement (issue #296, child 2 of #149), moved unchanged from the former
/// <c>InventoryApi.Services.InventoryCostService.ApplyMovement</c>: it rejects a negative purchase
/// cost, costs the movement through <see cref="StockMovementCostPolicy"/> (which also guards
/// against negative physical stock), and stages the product's new physical stock with the costed
/// movement through <see cref="IInventoryMovementStore"/>. It never saves: the caller saves and
/// triggers <see cref="IRebuildProductCost"/> inside its own transaction exactly as before.
/// </summary>
public sealed class RecordInventoryMovement : IRecordInventoryMovement
{
    private readonly IInventoryMovementStore _store;

    public RecordInventoryMovement(IInventoryMovementStore store) => _store = store;

    public async Task<IStagedInventoryMovement> RecordAsync(InventoryMovement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        var state = await _store.GetCostStateAsync(movement.ProductId, cancellationToken)
            ?? throw new InvalidOperationException($"Product {movement.ProductId} was not loaded.");

        if (movement.PurchaseUnitCost is < 0)
            throw new InvalidOperationException($"Purchase cost cannot be negative for product {movement.ProductId}.");

        var cost = StockMovementCostPolicy.Calculate(
            state.QuantityInStock,
            movement.QuantityChange,
            movement.Reason,
            state.CostingQuantity,
            state.AverageUnitCost,
            movement.PurchaseUnitCost);

        return _store.Stage(new CostedInventoryMovement(
            movement.ProductId,
            movement.QuantityChange,
            cost.NewStockQuantity,
            movement.Reason,
            cost.UnitCost,
            cost.TotalCost,
            movement.Notes,
            movement.MachineId,
            movement.EatBefore,
            movement.Source));
    }
}
