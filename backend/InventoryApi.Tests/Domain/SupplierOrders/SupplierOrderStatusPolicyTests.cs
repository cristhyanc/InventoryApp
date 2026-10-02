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
}
