using Inventory.Domain.Stock;

namespace Inventory.Application.Costing;

/// <summary>
/// A stock movement to record (issue #296). <see cref="PurchaseUnitCost"/> is only used to cost a
/// <see cref="StockAdjustmentReason.Restock"/> that the product's current average cannot cost (see
/// <see cref="Inventory.Domain.Costing.StockMovementCostPolicy"/>). <see cref="MachineId"/>,
/// <see cref="EatBefore"/> and <see cref="Source"/> are audit attributes the caller stamps on the
/// recorded movement; they never influence its cost.
/// </summary>
public sealed record InventoryMovement(
    long ProductId,
    int QuantityChange,
    StockAdjustmentReason Reason,
    string Notes,
    decimal? PurchaseUnitCost = null,
    long? MachineId = null,
    DateTime? EatBefore = null,
    StockAdjustmentSource Source = StockAdjustmentSource.Manual);

/// <summary>The product's current physical stock and costing state a new movement is costed against.</summary>
public sealed record InventoryMovementCostState(int QuantityInStock, int? CostingQuantity, decimal AverageUnitCost);

/// <summary>
/// A fully costed movement for <see cref="IInventoryMovementStore.Stage"/> to persist:
/// <see cref="QuantityAfter"/> is the product's new physical stock, and <see cref="UnitCost"/>/
/// <see cref="TotalCost"/> are the cost <see cref="Inventory.Domain.Costing.StockMovementCostPolicy"/>
/// assigned.
/// </summary>
public sealed record CostedInventoryMovement(
    long ProductId,
    int QuantityChange,
    int QuantityAfter,
    StockAdjustmentReason Reason,
    decimal? UnitCost,
    decimal? TotalCost,
    string Notes,
    long? MachineId,
    DateTime? EatBefore,
    StockAdjustmentSource Source);

/// <summary>
/// A movement staged in the caller's unit of work but not yet saved. <see cref="Id"/> is assigned
/// by the store when the caller saves its unit of work; read it only after that save.
/// </summary>
public interface IStagedInventoryMovement
{
    int Id { get; }

    int QuantityAfter { get; }
}
