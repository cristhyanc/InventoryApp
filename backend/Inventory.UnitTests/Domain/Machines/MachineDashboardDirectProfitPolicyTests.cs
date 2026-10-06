using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

public class MachineDashboardDirectProfitPolicyTests
{
    [Fact]
    public void Unavailable_when_resolution_failed()
    {
        var result = MachineDashboardDirectProfitPolicy.Calculate(
            new MachineDashboardDirectProfitInputs(true, 100m, false, 5m));

        Assert.Null(result);
    }

    [Fact]
    public void Unavailable_when_fee_rate_is_missing()
    {
        var result = MachineDashboardDirectProfitPolicy.Calculate(
            new MachineDashboardDirectProfitInputs(false, 100m, true, 0m));

        Assert.Null(result);
    }

    [Fact]
    public void Subtracts_fees_including_gst_from_net_sales()
    {
        var result = MachineDashboardDirectProfitPolicy.Calculate(
            new MachineDashboardDirectProfitInputs(false, 100m, false, 12.5m));

        Assert.Equal(87.5m, result);
    }
}
