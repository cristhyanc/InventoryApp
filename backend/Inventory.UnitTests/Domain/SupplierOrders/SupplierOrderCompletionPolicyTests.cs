using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Domain.SupplierOrders;

/// <summary>
/// The eligibility and projection rules behind "complete this order with whatever stays
/// undelivered" (issue #974). Both the preview an operator sees and the purchase save's own
/// re-validation call these, so what is asserted here is what both of them do.
/// </summary>
public class SupplierOrderCompletionPolicyTests
{
    [Theory]
    [InlineData(SupplierOrderStatus.Ordered, true)]
    [InlineData(SupplierOrderStatus.PartiallyReceived, true)]
    [InlineData(SupplierOrderStatus.Received, false)]
    [InlineData(SupplierOrderStatus.Cancelled, false)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered, false)]
    public void Only_an_open_order_may_be_completed(SupplierOrderStatus status, bool eligible) =>
        Assert.Equal(eligible, SupplierOrderCompletionPolicy.IsEligibleForCompletion(status));

    [Theory]
    [InlineData(SupplierOrderStatus.Ordered, false)]
    [InlineData(SupplierOrderStatus.PartiallyReceived, false)]
    [InlineData(SupplierOrderStatus.Received, false)]
    [InlineData(SupplierOrderStatus.Cancelled, true)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered, true)]
    public void A_status_a_person_decided_is_protected_from_the_rollup(SupplierOrderStatus status, bool preserved) =>
        Assert.Equal(preserved, SupplierOrderCompletionPolicy.PreservesStatus(status));

    [Fact]
    public void Completion_is_its_own_state_and_is_neither_cancellation_nor_full_receipt()
    {
        Assert.Equal(SupplierOrderStatus.ClosedPartiallyDelivered, SupplierOrderCompletionPolicy.CompletedStatus);
        Assert.True(SupplierOrderCompletionPolicy.IsExplicitlyCompleted(SupplierOrderStatus.ClosedPartiallyDelivered));
        Assert.False(SupplierOrderCompletionPolicy.IsExplicitlyCompleted(SupplierOrderStatus.Cancelled));
        Assert.False(SupplierOrderCompletionPolicy.IsExplicitlyCompleted(SupplierOrderStatus.Received));
    }

    /// <summary>
    /// The headline case: a final delivery that is short of the order leaves exactly the shortfall
    /// outstanding, counting the receipts that already landed.
    /// </summary>
    [Fact]
    public void A_short_final_delivery_leaves_the_shortfall_outstanding()
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            SupplierOrderStatus.PartiallyReceived,
            [new SupplierOrderLineFulfillment(11, 24m, 6m)],
            new Dictionary<int, decimal> { [11] = 10m });

        var line = Assert.Single(projected);
        Assert.Equal((11, 24m, 6m, 10m, 8m),
            (line.LineId, line.QuantityOrdered, line.QuantityAlreadyReceived, line.QuantityBeingReceived,
                line.OutstandingQuantity));
        Assert.True(SupplierOrderCompletionPolicy.HasOutstandingBalance(projected));
    }

    /// <summary>A line this purchase leaves out entirely is still owed in full - nothing infers a receipt.</summary>
    [Fact]
    public void An_omitted_line_stays_outstanding_in_full()
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            SupplierOrderStatus.PartiallyReceived,
            [new SupplierOrderLineFulfillment(11, 24m, 24m), new SupplierOrderLineFulfillment(12, 10m, 0m)],
            new Dictionary<int, decimal>());

        Assert.Equal([0m, 10m], projected.Select(line => line.OutstandingQuantity));
        Assert.Equal([11, 12], projected.Select(line => line.LineId));
        Assert.True(SupplierOrderCompletionPolicy.HasOutstandingBalance(projected));
    }

    [Fact]
    public void A_purchase_that_completes_every_line_leaves_no_outstanding_balance()
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            SupplierOrderStatus.PartiallyReceived,
            [new SupplierOrderLineFulfillment(11, 24m, 6m), new SupplierOrderLineFulfillment(12, 10m, 10m)],
            new Dictionary<int, decimal> { [11] = 18m });

        Assert.All(projected, line => Assert.Equal(0m, line.OutstandingQuantity));
        Assert.False(SupplierOrderCompletionPolicy.HasOutstandingBalance(projected));
    }

    /// <summary>More arrived than was ordered: the line is owed nothing, never a negative amount.</summary>
    [Fact]
    public void An_over_received_line_is_owed_nothing_rather_than_a_negative_amount()
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            SupplierOrderStatus.PartiallyReceived,
            [new SupplierOrderLineFulfillment(11, 24m, 20m)],
            new Dictionary<int, decimal> { [11] = 8m });

        Assert.Equal(0m, Assert.Single(projected).OutstandingQuantity);
        Assert.False(SupplierOrderCompletionPolicy.HasOutstandingBalance(projected));
    }

    /// <summary>
    /// Fractional pack-to-unit quantities, which the order and the purchase both store at
    /// <c>decimal(18,4)</c>, project without a rounding step of their own: the projection is
    /// subtraction over the quantities the receipt rules already decided.
    /// </summary>
    [Fact]
    public void A_fractional_quantity_projects_exactly()
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            SupplierOrderStatus.Ordered,
            [new SupplierOrderLineFulfillment(11, 2.5m, 0.75m)],
            new Dictionary<int, decimal> { [11] = 1.2500m });

        Assert.Equal(0.5m, Assert.Single(projected).OutstandingQuantity);
    }

    /// <summary>
    /// An order that is already closed is owed nothing at all, whatever its lines say - so a stale
    /// preview over one cannot report an outstanding balance to close.
    /// </summary>
    [Theory]
    [InlineData(SupplierOrderStatus.Cancelled)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered)]
    public void A_closed_order_projects_no_outstanding_balance(SupplierOrderStatus closed)
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            closed,
            [new SupplierOrderLineFulfillment(11, 24m, 6m)],
            new Dictionary<int, decimal>());

        Assert.Equal(0m, Assert.Single(projected).OutstandingQuantity);
        Assert.False(SupplierOrderCompletionPolicy.HasOutstandingBalance(projected));
    }

    [Fact]
    public void An_order_with_no_lines_has_no_outstanding_balance()
    {
        var projected = SupplierOrderCompletionPolicy.Project(
            SupplierOrderStatus.Ordered, [], new Dictionary<int, decimal>());

        Assert.Empty(projected);
        Assert.False(SupplierOrderCompletionPolicy.HasOutstandingBalance(projected));
    }

    /// <summary>
    /// The refusal names every completed order in the way, so an operator can see which one blocks
    /// the correction rather than being told only that something does.
    /// </summary>
    [Fact]
    public void The_linked_purchase_refusal_names_the_completed_orders()
    {
        var message = SupplierOrderCompletionPolicy.LinkedToCompletedOrderMessage([9, 4, 9]);

        Assert.Contains("#4, #9", message, StringComparison.Ordinal);
        Assert.DoesNotContain("#9, #4", message, StringComparison.Ordinal);
    }
}
