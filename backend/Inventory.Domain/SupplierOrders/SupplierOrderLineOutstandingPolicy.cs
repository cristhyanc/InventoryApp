namespace Inventory.Domain.SupplierOrders;

/// <summary>
/// How much of a supplier-order line is still expected to arrive. The one authoritative
/// implementation of the rule <c>InventoryApi.Models.SupplierOrderLine.OutstandingQuantity</c>
/// computed inline before issue #304 gave the supplier-order endpoints an API-owned response DTO:
/// a cancelled order is owed nothing at all, and an over-received line is owed nothing rather than
/// a negative amount. The entity and the response DTO both call it, so the value a client reads
/// cannot drift from the value the persistence model reports.
///
/// This is deliberately not the aggregate on-order quantity the reorder calculation uses
/// (<c>IOutstandingSupplierOrderQuantityStore</c>), which sums unreceived quantities per product in
/// SQL across every order that is neither cancelled nor received.
/// </summary>
public static class SupplierOrderLineOutstandingPolicy
{
    public static decimal Outstanding(SupplierOrderStatus orderStatus, decimal quantityOrdered, decimal quantityReceived) =>
        orderStatus == SupplierOrderStatus.Cancelled
            ? 0m
            : Math.Max(0m, quantityOrdered - quantityReceived);
}
