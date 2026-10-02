namespace Inventory.Domain.Stock;

/// <summary>
/// Mirrors <c>InventoryApi.Models.StockAdjustmentSource</c> member-for-member (issue #282),
/// distinguishing an operator-entered movement from one imported and applied from a Nayax
/// machine-stock event (issue #183) even though both may use
/// <see cref="StockAdjustmentReason.MachineRefill"/>. These numeric values are persisted on
/// <c>StockAdjustment.Source</c> and must not change.
/// </summary>
public enum StockAdjustmentSource
{
    Manual = 0,
    Nayax = 1
}
