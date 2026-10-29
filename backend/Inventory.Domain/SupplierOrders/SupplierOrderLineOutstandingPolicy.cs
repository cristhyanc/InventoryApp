namespace Inventory.Domain.SupplierOrders;

/// <summary>
/// How much of a supplier-order line is still expected to arrive. The one authoritative
/// implementation of the rule <c>Inventory.Infrastructure.Models.SupplierOrderLine.OutstandingQuantity</c>
/// computed inline before issue #304 gave the supplier-order endpoints an API-owned response DTO:
/// a cancelled order is owed nothing at all, and an over-received line is owed nothing rather than
/// a negative amount. The entity and the response DTO both call it, so the value a client reads
/// cannot drift from the value the persistence model reports.
///
/// <para>Since issue #974 an order the operator explicitly completed with part of it undelivered is
/// owed nothing either, for the same reason a cancelled one is not: nothing more is coming. What was
/// ordered and never arrived stays readable as <see cref="Undelivered"/>, which is a closed balance
/// rather than an expectation - so a figure that answers "how much is still on its way" and a figure
/// that answers "how much never came" can never be read as each other.</para>
///
/// This is deliberately not the aggregate on-order quantity the reorder calculation uses
/// (<c>IOutstandingSupplierOrderQuantityStore</c>), which sums unreceived quantities per product in
/// SQL across every order that is neither cancelled nor received nor explicitly completed.
/// </summary>
public static class SupplierOrderLineOutstandingPolicy
{
    public static decimal Outstanding(SupplierOrderStatus orderStatus, decimal quantityOrdered, decimal quantityReceived) =>
        orderStatus is SupplierOrderStatus.Cancelled or SupplierOrderStatus.ClosedPartiallyDelivered
            ? 0m
            : Unreceived(quantityOrdered, quantityReceived);

    /// <summary>
    /// What was ordered on this line and will never be delivered (issue #974). It is non-zero only
    /// for an order that was explicitly completed with an undelivered balance: while an order is
    /// still open the unreceived quantity is <see cref="Outstanding"/> - still expected - and a
    /// cancelled order's abandoned quantity keeps the meaning it has always had rather than being
    /// restated as a delivery that failed.
    /// </summary>
    public static decimal Undelivered(SupplierOrderStatus orderStatus, decimal quantityOrdered, decimal quantityReceived) =>
        orderStatus == SupplierOrderStatus.ClosedPartiallyDelivered
            ? Unreceived(quantityOrdered, quantityReceived)
            : 0m;

    private static decimal Unreceived(decimal quantityOrdered, decimal quantityReceived) =>
        Math.Max(0m, quantityOrdered - quantityReceived);
}
