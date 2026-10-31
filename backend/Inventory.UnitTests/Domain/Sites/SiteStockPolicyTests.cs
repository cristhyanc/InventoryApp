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

    /// <summary>
    /// Issue #1013: one machine's own percentage is the site rule applied to that machine's
    /// selections alone - the same sum of (PAR - MissingStockByMDB) over the same sum of PAR, so a
    /// machine figure and the site figure above it can never disagree about what "stocked" means.
    /// </summary>
    [Fact]
    public void Machine_stock_percentage_divides_in_stock_by_that_machines_own_capacity()
    {
        var facts = new[]
        {
            new SiteMachineProductStockFact(1, true, Par: 10, MissingStockByMdb: 5, VendOutAlertThreshold: 1),
            new SiteMachineProductStockFact(2, true, Par: 10, MissingStockByMdb: 10, VendOutAlertThreshold: 1),
        };

        Assert.Equal(25m, SiteStockPolicy.CalculateMachineStockPercentage(facts));
    }

    [Fact]
    public void Machine_stock_percentage_is_zero_when_the_machine_holds_none_of_its_capacity()
    {
        var facts = new[]
        {
            new SiteMachineProductStockFact(1, true, Par: 6, MissingStockByMdb: 6, VendOutAlertThreshold: 1),
        };

        Assert.Equal(0m, SiteStockPolicy.CalculateMachineStockPercentage(facts));
    }

    /// <summary>
    /// A machine with no capacity at all is "no capacity", never 0% and never the site rule's 100%:
    /// nothing is known about how full it is, so there is no percentage to state. The site rule
    /// itself is deliberately left exactly as it was.
    /// </summary>
    [Fact]
    public void Machine_stock_percentage_is_null_when_the_machine_reports_no_capacity()
    {
        Assert.Null(SiteStockPolicy.CalculateMachineStockPercentage([]));

        var withoutPar = new[]
        {
            new SiteMachineProductStockFact(1, true, Par: 0, MissingStockByMdb: 0, VendOutAlertThreshold: 0),
            new SiteMachineProductStockFact(2, true, Par: 0, MissingStockByMdb: 0, VendOutAlertThreshold: 0),
        };

        Assert.Null(SiteStockPolicy.CalculateMachineStockPercentage(withoutPar));
        Assert.Equal(100m, SiteStockPolicy.CalculateStockPercentage(withoutPar));
    }

    /// <summary>
    /// More missing than the machine was ever stocked for is reported as the negative figure the
    /// arithmetic gives, exactly as the site rule reports it today. It is a provider-data
    /// contradiction rather than a figure to clamp, and silently correcting it here would hide it
    /// from the only place that can notice.
    /// </summary>
    [Fact]
    public void Machine_stock_percentage_reports_more_missing_than_par_as_the_site_rule_does()
    {
        var facts = new[]
        {
            new SiteMachineProductStockFact(1, true, Par: 4, MissingStockByMdb: 6, VendOutAlertThreshold: 1),
        };

        Assert.Equal(-50m, SiteStockPolicy.CalculateMachineStockPercentage(facts));
        Assert.Equal(-50m, SiteStockPolicy.CalculateStockPercentage(facts));
    }

    /// <summary>
    /// A selection that resolves to no catalogue product, or to an inactive one, still counts toward
    /// the machine's physical capacity - the same thing the site percentage has always done, and the
    /// reason the alert counts and the percentage read the same facts differently.
    /// </summary>
    [Fact]
    public void Machine_stock_percentage_counts_unmapped_and_inactive_selections()
    {
        var facts = new[]
        {
            new SiteMachineProductStockFact(null, false, Par: 10, MissingStockByMdb: 2, VendOutAlertThreshold: 0),
            new SiteMachineProductStockFact(3, false, Par: 10, MissingStockByMdb: 0, VendOutAlertThreshold: 0),
        };

        Assert.Equal(90m, SiteStockPolicy.CalculateMachineStockPercentage(facts));
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
