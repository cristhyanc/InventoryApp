using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Domain.SupplierOrders;

/// <summary>
/// The one rule behind "how much of this line is still coming" and, since issue #974, "how much of
/// it never came". The two are deliberately separate answers: a client that shows an outstanding
/// quantity must not read a closed balance as an expectation, and a client that shows an undelivered
/// quantity must not read an open order's unreceived quantity as a failed delivery.
/// </summary>
public class SupplierOrderLineOutstandingPolicyTests
{
    [Theory]
    [InlineData(SupplierOrderStatus.Ordered, 24, 0, 24)]
    [InlineData(SupplierOrderStatus.PartiallyReceived, 24, 6, 18)]
    [InlineData(SupplierOrderStatus.Received, 24, 24, 0)]
    [InlineData(SupplierOrderStatus.Received, 24, 30, 0)]
    [InlineData(SupplierOrderStatus.Cancelled, 24, 0, 0)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered, 24, 18, 0)]
    public void Outstanding_is_what_is_still_expected_to_arrive(
        SupplierOrderStatus status, decimal ordered, decimal received, decimal expected) =>
        Assert.Equal(expected, SupplierOrderLineOutstandingPolicy.Outstanding(status, ordered, received));

    [Theory]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered, 24, 18, 6)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered, 24, 0, 24)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered, 24, 30, 0)]
    // Still open, so the unreceived remainder is an expectation and not a failed delivery.
    [InlineData(SupplierOrderStatus.Ordered, 24, 0, 0)]
    [InlineData(SupplierOrderStatus.PartiallyReceived, 24, 6, 0)]
    [InlineData(SupplierOrderStatus.Received, 24, 24, 0)]
    // A cancellation keeps the meaning it has always had, rather than being restated as undelivered.
    [InlineData(SupplierOrderStatus.Cancelled, 24, 6, 0)]
    public void Undelivered_is_only_what_an_explicit_completion_closed(
        SupplierOrderStatus status, decimal ordered, decimal received, decimal expected) =>
        Assert.Equal(expected, SupplierOrderLineOutstandingPolicy.Undelivered(status, ordered, received));

    /// <summary>
    /// A completed order's two figures never both answer the same question: what is still coming is
    /// nothing, and what never came is the balance - so a screen cannot double-count them.
    /// </summary>
    [Fact]
    public void A_completed_orders_outstanding_and_undelivered_quantities_do_not_overlap()
    {
        const SupplierOrderStatus completed = SupplierOrderStatus.ClosedPartiallyDelivered;

        Assert.Equal(0m, SupplierOrderLineOutstandingPolicy.Outstanding(completed, 24m, 18m));
        Assert.Equal(6m, SupplierOrderLineOutstandingPolicy.Undelivered(completed, 24m, 18m));
    }
}
