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

    /// <summary>
    /// The operator recorded the final delivery and decided no more of this order will arrive
    /// (issue #974). It is neither <see cref="Cancelled"/> - goods really were delivered and are
    /// recorded - nor <see cref="Received"/> - part of the order never arrived. The ordered
    /// quantities, the actual receipts and the purchase links stay exactly as they were, so the
    /// undelivered balance remains inspectable history; it simply stops counting as supply that is
    /// still on its way.
    /// </summary>
    ClosedPartiallyDelivered,
}
