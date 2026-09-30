using Inventory.Domain.Sites;
using Xunit;

namespace InventoryApi.Tests.Domain.Sites;

public class SiteStockPolicyTests
{
    [Fact]
    public void Stock_percentage_is_100_when_the_site_has_no_capacity()
    {
        Assert.Equal(100m, SiteStockPolicy.CalculateStockPercentage([]));
    }

    [Fact]
    public void Stock_percentage_divides_in_stock_by_capacity_across_every_machine()
    {
        var facts = new[]
        {
            new SiteMachineProductStockFact(1, true, Par: 10, MissingStockByMdb: 5, VendOutAlertThreshold: 1),
            new SiteMachineProductStockFact(2, true, Par: 10, MissingStockByMdb: 10, VendOutAlertThreshold: 1),
        };

        Assert.Equal(25m, SiteStockPolicy.CalculateStockPercentage(facts));
    }

    [Fact]
    public void Alert_counts_ignore_inactive_and_unmapped_products()
    {
        var facts = new[]
        {
            new SiteMachineProductStockFact(1, true, Par: 5, MissingStockByMdb: 4, VendOutAlertThreshold: 1),
            new SiteMachineProductStockFact(2, true, Par: 5, MissingStockByMdb: 5, VendOutAlertThreshold: 1),
            new SiteMachineProductStockFact(3, false, Par: 5, MissingStockByMdb: 5, VendOutAlertThreshold: 1),
            new SiteMachineProductStockFact(null, true, Par: 5, MissingStockByMdb: 5, VendOutAlertThreshold: 1),
        };

        var (low, empty) = SiteStockPolicy.CalculateAlertCounts(facts);

        Assert.Equal(1, low);
        Assert.Equal(1, empty);
    }
}
