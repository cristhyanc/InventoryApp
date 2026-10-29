namespace Inventory.Domain.SupplierOrders;

/// <summary>
/// Rolls a supplier order's lines' received/ordered quantities up into its overall fulfillment
/// status. Mirrors the former
/// <c>InventoryApi.Services.PurchaseService.RecalculateSupplierOrderFulfillmentAsync</c> status
/// rule exactly: fully received when every line is fully received, partially received when any
/// line has received anything, otherwise still ordered. Never returns
/// <see cref="SupplierOrderStatus.Cancelled"/> or
/// <see cref="SupplierOrderStatus.ClosedPartiallyDelivered"/>: those two are a person's decision
/// rather than a function of the quantities, and the overload taking the current status is what keeps
/// a rollup from overwriting one.
/// </summary>
public static class SupplierOrderStatusPolicy
{
    /// <summary>
    /// The rollup, applied only where it has authority: an order whose status
    /// <see cref="SupplierOrderCompletionPolicy.PreservesStatus"/> protects - cancelled, or
    /// explicitly completed with an undelivered balance (issue #974) - keeps the status it has,
    /// however its lines' quantities have moved. Write paths use this overload rather than guarding
    /// the call themselves, so a protected status cannot be forgotten at one of them.
    /// </summary>
    public static SupplierOrderStatus Resolve(
        SupplierOrderStatus currentStatus,
        IEnumerable<(decimal QuantityOrdered, decimal QuantityReceived)> lines) =>
        SupplierOrderCompletionPolicy.PreservesStatus(currentStatus) ? currentStatus : Resolve(lines);

    public static SupplierOrderStatus Resolve(IEnumerable<(decimal QuantityOrdered, decimal QuantityReceived)> lines)
    {
        var lineList = lines as IReadOnlyCollection<(decimal QuantityOrdered, decimal QuantityReceived)> ?? lines.ToList();

        if (lineList.All(line => line.QuantityReceived >= line.QuantityOrdered))
            return SupplierOrderStatus.Received;

        return lineList.Any(line => line.QuantityReceived > 0)
            ? SupplierOrderStatus.PartiallyReceived
            : SupplierOrderStatus.Ordered;
    }
}
