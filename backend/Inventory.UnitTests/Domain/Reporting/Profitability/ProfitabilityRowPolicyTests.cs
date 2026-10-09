using Inventory.Domain.Reporting.Profitability;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Profitability;

public class ProfitabilityRowPolicyTests
{
    [Fact]
    public void Complete_cogs_returns_cost_gross_profit_and_margin()
    {
        var result = ProfitabilityRowPolicy.Calculate(new ProfitabilityRowInputs(100m, 40m, true));

        Assert.Equal(40m, result.CostOfGoods);
        Assert.Equal(60m, result.GrossProfit);
        Assert.Equal(60m, result.MarginPercent);
    }

    [Fact]
    public void Incomplete_cogs_makes_cost_profit_and_margin_null_never_zero()
    {
        var result = ProfitabilityRowPolicy.Calculate(new ProfitabilityRowInputs(100m, 40m, false));

        Assert.Null(result.CostOfGoods);
        Assert.Null(result.GrossProfit);
        Assert.Null(result.MarginPercent);
    }

    [Fact]
    public void Zero_sales_does_not_throw_and_reports_zero_margin()
    {
        var result = ProfitabilityRowPolicy.Calculate(new ProfitabilityRowInputs(0m, 0m, true));

        Assert.Equal(0m, result.GrossProfit);
        Assert.Equal(0m, result.MarginPercent);
    }
}
