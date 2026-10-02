using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Costing;

/// <summary>The resulting physical stock and the cost carried by one stock movement.</summary>
public sealed record StockMovementCost(int NewStockQuantity, decimal? UnitCost, decimal? TotalCost);

/// <summary>
/// The unit/total cost a new stock movement carries, and the guard against negative physical
/// stock. Extracted unchanged from the former inline logic of
/// <c>InventoryApi.Services.InventoryCostService.ApplyMovement</c> (issue #295, child 1 of #149):
/// an outgoing movement is costed at the product's current average unit cost when its costing
/// quantity is positive and that average is non-negative; otherwise a restock is costed at its
/// purchase unit cost; otherwise the movement carries no cost.
/// </summary>
public static class StockMovementCostPolicy
{
    /// <exception cref="InsufficientStockException">The movement would make physical stock negative.</exception>
    /// <exception cref="OverflowException">The resulting physical stock is outside <see cref="int"/> range.</exception>
    public static StockMovementCost Calculate(
        int quantityInStock,
        int quantityChange,
        StockAdjustmentReason reason,
        int? costingQuantity,
        decimal averageUnitCost,
        decimal? purchaseUnitCost)
    {
        var newStockQuantity = checked(quantityInStock + quantityChange);
        if (newStockQuantity < 0)
            throw new InsufficientStockException(quantityInStock);

        var unitCost = quantityChange < 0 && costingQuantity is > 0 && averageUnitCost >= 0
            ? averageUnitCost
            : reason == StockAdjustmentReason.Restock
                ? purchaseUnitCost
                : null;

        return new(
            newStockQuantity,
            unitCost,
            unitCost.HasValue ? unitCost.Value * Math.Abs(quantityChange) : null);
    }
}
