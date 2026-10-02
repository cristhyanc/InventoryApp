using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// One stock movement as the Application layer sees it, mirroring every scalar field of
/// <c>InventoryApi.Models.StockAdjustment</c> the API response serializes (issue #282). Never
/// carries the <c>Product</c>/<c>ReceiptItem</c> navigations - the former
/// <c>StockController.History</c>/<c>Adjust</c> responses never serialized them either
/// (<c>[JsonIgnore]</c>).
/// </summary>
public sealed record StockAdjustmentRecord(
    int Id,
    int BusinessId,
    long ProductId,
    int? ReceiptItemId,
    int QuantityChange,
    int QuantityAfter,
    decimal? UnitCost,
    decimal? TotalCost,
    int? CostingQuantityAfter,
    decimal? AverageUnitCostAfter,
    decimal? InventoryValueAfter,
    StockAdjustmentReason Reason,
    StockAdjustmentSource Source,
    long? MachineId,
    string? Notes,
    DateTime? EatBefore,
    DateTime CreatedAt,
    DateTime EffectiveAt);
