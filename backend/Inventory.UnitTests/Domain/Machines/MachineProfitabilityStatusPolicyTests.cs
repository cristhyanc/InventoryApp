using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

public class MachineProfitabilityStatusPolicyTests
{
    [Fact]
    public void Missing_cogs_takes_priority_over_every_other_reason()
    {
        var status = MachineProfitabilityStatusPolicy.Determine(
            new MachineProfitabilityStatusInputs(true, true, false, true));

        Assert.Equal("Unavailable: one or more completed sales have no persisted COGS.", status);
    }

    [Fact]
    public void Reports_missing_site_mapping_only_when_completed_sales_exist()
    {
        Assert.Equal(
            "Unavailable: the machine is not mapped to a site.",
            MachineProfitabilityStatusPolicy.Determine(new MachineProfitabilityStatusInputs(false, true, false, false)));

        Assert.Null(MachineProfitabilityStatusPolicy.Determine(
            new MachineProfitabilityStatusInputs(false, false, false, false)));
    }

    [Fact]
    public void Reports_ambiguous_commission_coverage_only_when_mapped_to_a_site()
    {
        var status = MachineProfitabilityStatusPolicy.Determine(
            new MachineProfitabilityStatusInputs(false, true, true, true));

        Assert.Equal("Unavailable: commission agreement coverage is missing or ambiguous.", status);
    }

    [Fact]
    public void No_status_when_everything_resolves_cleanly()
    {
        var status = MachineProfitabilityStatusPolicy.Determine(
            new MachineProfitabilityStatusInputs(false, true, true, false));

        Assert.Null(status);
    }
}
