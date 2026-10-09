using Inventory.Domain.Reporting.Profitability;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Profitability;

public class MachineDirectProfitPolicyTests
{
    private static MachineDirectProfitInputs Complete(decimal sales = 100m, decimal cost = 40m,
        decimal feesIncludingGst = 5m, decimal siteCommission = 2m, decimal operatingExpenses = 1m) => new(
        Sales: sales,
        PartialCostOfGoods: cost,
        IsCogsComplete: true,
        FeesIncludingGst: feesIncludingGst,
        HasMissingFeeRates: false,
        SiteCommission: siteCommission,
        CommissionComplete: true,
        OperatingExpenses: operatingExpenses);

    [Fact]
    public void Complete_scope_returns_direct_profit_and_margin()
    {
        var result = MachineDirectProfitPolicy.Calculate(Complete());

        Assert.True(result.IsComplete);
        Assert.Equal(100m - 40m - 2m - 1m - 5m, result.DirectProfit);
        Assert.NotNull(result.DirectMarginPercent);
    }

    [Fact]
    public void Incomplete_cogs_makes_direct_profit_unavailable()
    {
        var inputs = Complete() with { IsCogsComplete = false };

        var result = MachineDirectProfitPolicy.Calculate(inputs);

        Assert.False(result.IsComplete);
        Assert.Null(result.DirectProfit);
        Assert.Null(result.DirectMarginPercent);
    }

    [Fact]
    public void Missing_fee_rates_make_direct_profit_unavailable_even_when_cogs_is_complete()
    {
        var inputs = Complete() with { HasMissingFeeRates = true };

        var result = MachineDirectProfitPolicy.Calculate(inputs);

        Assert.False(result.IsComplete);
        Assert.Null(result.DirectProfit);
    }

    [Fact]
    public void Incomplete_commission_makes_direct_profit_unavailable()
    {
        var inputs = Complete() with { CommissionComplete = false };

        var result = MachineDirectProfitPolicy.Calculate(inputs);

        Assert.False(result.IsComplete);
        Assert.Null(result.DirectProfit);
        Assert.Null(result.DirectMarginPercent);
    }

    [Fact]
    public void Zero_sales_does_not_throw_and_reports_zero_margin()
    {
        var inputs = Complete(sales: 0m, cost: 0m, feesIncludingGst: 0m, siteCommission: 0m, operatingExpenses: 0m);

        var result = MachineDirectProfitPolicy.Calculate(inputs);

        Assert.Equal(0m, result.DirectProfit);
        Assert.Equal(0m, result.DirectMarginPercent);
    }
}
