namespace Inventory.Domain.SupplierOrders;

/// <summary>One purchase line item being matched against outstanding supplier-order lines.</summary>
public readonly record struct PurchaseItemToAllocate(int PurchaseItemId, long ProductId, decimal Quantity);

/// <summary>
/// One candidate outstanding supplier-order line a purchase item's product could fulfil, already
/// filtered (same supplier, ordered on or before the purchase date, not cancelled/received) and
/// ordered (oldest order date first, then order id, then line id) by the caller.
/// </summary>
public readonly record struct CandidateOrderLine(int LineId, int SupplierOrderId, decimal OutstandingQuantity);

/// <summary>One unit of fulfillment a purchase line item applies to a supplier-order line.</summary>
public readonly record struct SupplierOrderAllocation(int SupplierOrderLineId, int SupplierOrderId, int ReceiptItemId, decimal QuantityApplied);

/// <summary>
/// Matches newly purchased quantities to outstanding supplier-order lines, oldest order first,
/// never over-allocating a line beyond its outstanding quantity even across several purchase line
/// items in the same call. Mirrors the former
/// <c>InventoryApi.Services.PurchaseService.AllocateSupplierOrderFulfillmentAsync</c> algorithm
/// exactly, given the same already-queried, already-filtered, already-ordered candidate lines.
/// </summary>
public static class SupplierOrderFulfillmentAllocationPolicy
{
    public static IReadOnlyList<SupplierOrderAllocation> Allocate(
        IEnumerable<PurchaseItemToAllocate> purchaseItems,
        IReadOnlyDictionary<long, IReadOnlyList<CandidateOrderLine>> candidateLinesByProduct)
    {
        var allocations = new List<SupplierOrderAllocation>();
        var allocatedByLineId = new Dictionary<int, decimal>();

        foreach (var purchaseItem in purchaseItems)
        {
            if (!candidateLinesByProduct.TryGetValue(purchaseItem.ProductId, out var lines)) continue;

            var remainingQuantity = purchaseItem.Quantity;
            foreach (var line in lines)
            {
                if (remainingQuantity <= 0) break;

                var outstandingQuantity = line.OutstandingQuantity - allocatedByLineId.GetValueOrDefault(line.LineId);
                if (outstandingQuantity <= 0) continue;

                var appliedQuantity = Math.Min(remainingQuantity, outstandingQuantity);
                allocations.Add(new SupplierOrderAllocation(line.LineId, line.SupplierOrderId, purchaseItem.PurchaseItemId, appliedQuantity));
                allocatedByLineId[line.LineId] = allocatedByLineId.GetValueOrDefault(line.LineId) + appliedQuantity;
                remainingQuantity -= appliedQuantity;
            }
        }

        return allocations;
    }
}
