using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Domain.SupplierOrders;

public class SupplierOrderStatusPolicyTests
{
    [Fact]
    public void No_received_quantity_is_still_ordered()
    {
        var status = SupplierOrderStatusPolicy.Resolve([(10m, 0m)]);

        Assert.Equal(SupplierOrderStatus.Ordered, status);
    }

    [Fact]
    public void Some_but_not_all_received_is_partially_received()
    {
        var status = SupplierOrderStatusPolicy.Resolve([(10m, 4m)]);

        Assert.Equal(SupplierOrderStatus.PartiallyReceived, status);
    }

    [Fact]
    public void Every_line_fully_received_is_received()
    {
        var status = SupplierOrderStatusPolicy.Resolve([(10m, 10m), (5m, 5m)]);

        Assert.Equal(SupplierOrderStatus.Received, status);
    }

    [Fact]
    public void One_line_short_of_fully_received_is_partially_received()
    {
        var status = SupplierOrderStatusPolicy.Resolve([(10m, 10m), (5m, 4m)]);

        Assert.Equal(SupplierOrderStatus.PartiallyReceived, status);
    }

    [Fact]
    public void A_line_received_beyond_its_ordered_quantity_still_counts_as_fully_received()
    {
        var status = SupplierOrderStatusPolicy.Resolve([(10m, 12m)]);

        Assert.Equal(SupplierOrderStatus.Received, status);
    }

    /// <summary>
    /// The two statuses a person decides survive the rollup, however the quantities move (issue
    /// #974). A completed order whose lines are short would otherwise roll back to
    /// <c>PartiallyReceived</c> on the next write that touched it, silently reopening a closed
    /// balance; a cancelled one has always been protected, and this is the same protection.
    /// </summary>
    [Theory]
    [InlineData(SupplierOrderStatus.Cancelled)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered)]
    public void A_status_a_person_decided_survives_the_rollup(SupplierOrderStatus decided)
    {
        Assert.Equal(decided, SupplierOrderStatusPolicy.Resolve(decided, [(10m, 4m)]));
        Assert.Equal(decided, SupplierOrderStatusPolicy.Resolve(decided, [(10m, 0m)]));
        Assert.Equal(decided, SupplierOrderStatusPolicy.Resolve(decided, [(10m, 10m)]));
    }

    [Theory]
    [InlineData(SupplierOrderStatus.Ordered)]
    [InlineData(SupplierOrderStatus.PartiallyReceived)]
    [InlineData(SupplierOrderStatus.Received)]
    public void An_open_or_received_order_is_still_rolled_up_from_its_quantities(SupplierOrderStatus current)
    {
        Assert.Equal(SupplierOrderStatus.PartiallyReceived, SupplierOrderStatusPolicy.Resolve(current, [(10m, 4m)]));
        Assert.Equal(SupplierOrderStatus.Received, SupplierOrderStatusPolicy.Resolve(current, [(10m, 10m)]));
    }
}
