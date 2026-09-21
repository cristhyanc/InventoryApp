using Inventory.Domain.Reporting;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting;

public class ReportingCalculationsTests
{
    [Fact]
    public void GrossProfit_subtracts_cost_from_sales()
    {
        Assert.Equal(6m, ReportingCalculations.GrossProfit(10m, 4m));
    }

    [Fact]
    public void MarginPercent_returns_zero_when_sales_are_zero()
    {
        Assert.Equal(0m, ReportingCalculations.MarginPercent(0m, 4m));
    }

    [Fact]
    public void MarginPercent_computes_gross_profit_over_sales()
    {
        Assert.Equal(40m, ReportingCalculations.MarginPercent(10m, 6m));
    }

    [Fact]
    public void PercentageOf_returns_zero_when_denominator_is_zero()
    {
        Assert.Equal(0m, ReportingCalculations.PercentageOf(5m, 0m));
    }

    [Fact]
    public void PercentageOf_computes_amount_over_denominator()
    {
        Assert.Equal(25m, ReportingCalculations.PercentageOf(5m, 20m));
    }

    [Fact]
    public void GstFromInclusive_extracts_ten_percent_of_a_gst_inclusive_amount()
    {
        Assert.Equal(10m, ReportingCalculations.GstFromInclusive(110m));
    }

    [Fact]
    public void GstFromExcluding_adds_ten_percent_on_a_gst_exclusive_amount()
    {
        Assert.Equal(10m, ReportingCalculations.GstFromExcluding(100m));
    }

    [Fact]
    public void Average_returns_zero_when_count_is_zero()
    {
        Assert.Equal(0m, ReportingCalculations.Average(100m, 0));
    }

    [Fact]
    public void Average_divides_amount_by_count()
    {
        Assert.Equal(25m, ReportingCalculations.Average(100m, 4));
    }
}
