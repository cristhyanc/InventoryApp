namespace Inventory.Domain.Stock;

/// <summary>
/// Mirrors <c>Inventory.Infrastructure.Models.StockAdjustmentReason</c> member-for-member (issue #282, the
/// #240 stock-adjustment vocabulary handoff), so Domain/Application never reference the InventoryApi
/// persistence enum directly - the same convention <c>Inventory.Domain.Expenses.ExpenseCategory</c>
/// and <c>Inventory.Domain.SupplierOrders.SupplierOrderStatus</c> already established for their own
/// persistence enums. These numeric values are persisted on <c>StockAdjustment.Reason</c> and must
/// not change.
/// </summary>
public enum StockAdjustmentReason
{
    Restock = 0,
    Sale = 1,
    Damaged = 2,
    Expired = 3,
    Correction = 4,
    MachineRefill = 5
}
