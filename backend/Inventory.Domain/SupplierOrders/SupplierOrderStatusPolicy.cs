namespace Inventory.Domain.SupplierOrders;

/// <summary>
/// Rolls a supplier order's lines' received/ordered quantities up into its overall fulfillment
/// status. Mirrors the former
/// <c>InventoryApi.Services.PurchaseService.RecalculateSupplierOrderFulfillmentAsync</c> status
/// rule exactly: fully received when every line is fully received, partially received when any
/// line has received anything, otherwise still ordered. Never returns
/// <see cref="SupplierOrderStatus.Cancelled"/>: the caller is responsible for leaving a cancelled
/// order's status untouched, exactly as the former rule did by skipping this computation for one.
/// </summary>
public static class SupplierOrderStatusPolicy
{
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
