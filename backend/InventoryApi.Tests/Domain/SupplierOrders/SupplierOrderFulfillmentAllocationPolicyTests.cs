using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Domain.SupplierOrders;

public class SupplierOrderFulfillmentAllocationPolicyTests
{
    [Fact]
    public void A_purchase_exactly_matching_an_outstanding_line_fully_allocates_it()
    {
        var purchaseItems = new[] { new PurchaseItemToAllocate(1, 100, 24m) };
        var candidates = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>
        {
            [100] = [new CandidateOrderLine(1, 1, 24m)],
        };

        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItems, candidates);

        var allocation = Assert.Single(allocations);
        Assert.Equal(1, allocation.SupplierOrderLineId);
        Assert.Equal(1, allocation.ReceiptItemId);
        Assert.Equal(24m, allocation.QuantityApplied);
    }

    [Fact]
    public void A_partial_purchase_only_allocates_what_was_purchased()
    {
        var purchaseItems = new[] { new PurchaseItemToAllocate(1, 100, 18m) };
        var candidates = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>
        {
            [100] = [new CandidateOrderLine(1, 1, 24m)],
        };

        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItems, candidates);

        Assert.Equal(18m, Assert.Single(allocations).QuantityApplied);
    }

    [Fact]
    public void An_excess_purchase_caps_allocation_at_the_outstanding_quantity()
    {
        var purchaseItems = new[] { new PurchaseItemToAllocate(1, 100, 10m) };
        var candidates = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>
        {
            [100] = [new CandidateOrderLine(1, 1, 6m)],
        };

        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItems, candidates);

        Assert.Equal(6m, Assert.Single(allocations).QuantityApplied);
    }

    [Fact]
    public void Multiple_open_orders_consume_the_oldest_outstanding_line_first()
    {
        var purchaseItems = new[] { new PurchaseItemToAllocate(1, 100, 12m) };
        // Candidate order is pre-sorted oldest-first by the caller, the same contract the EF adapter's query fulfils.
        var candidates = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>
        {
            [100] = [new CandidateOrderLine(1, 1, 10m), new CandidateOrderLine(2, 2, 10m)],
        };

        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItems, candidates);

        Assert.Equal(2, allocations.Count);
        Assert.Equal(10m, allocations[0].QuantityApplied);
        Assert.Equal(1, allocations[0].SupplierOrderLineId);
        Assert.Equal(2m, allocations[1].QuantityApplied);
        Assert.Equal(2, allocations[1].SupplierOrderLineId);
    }

    [Fact]
    public void Duplicate_purchase_lines_for_the_same_product_never_over_allocate_a_line()
    {
        var purchaseItems = new[]
        {
            new PurchaseItemToAllocate(1, 100, 8m),
            new PurchaseItemToAllocate(2, 100, 8m),
        };
        var candidates = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>
        {
            [100] = [new CandidateOrderLine(1, 1, 10m)],
        };

        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItems, candidates);

        Assert.Equal(2, allocations.Count);
        Assert.Equal(8m, allocations[0].QuantityApplied);
        Assert.Equal(2m, allocations[1].QuantityApplied);
        Assert.Equal(10m, allocations.Sum(a => a.QuantityApplied));
    }

    [Fact]
    public void A_product_with_no_candidate_lines_allocates_nothing()
    {
        var purchaseItems = new[] { new PurchaseItemToAllocate(1, 999, 5m) };
        var candidates = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>();

        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItems, candidates);

        Assert.Empty(allocations);
    }
}
