namespace Inventory.Domain.SupplierOrders;

/// <summary>
/// Mirrors <c>Inventory.Infrastructure.Models.SupplierOrderStatus</c> member-for-member (the same convention
/// <c>Inventory.Domain.Expenses.ExpenseCategory</c> uses for
/// <c>Inventory.Infrastructure.Models.OperatingExpenseCategory</c>), so Domain/Application never reference the
/// InventoryApi enum; the two convert by a plain cast at the persistence adapter boundary.
/// </summary>
public enum SupplierOrderStatus
{
    Ordered,
    PartiallyReceived,
    Received,
    Cancelled,
}
