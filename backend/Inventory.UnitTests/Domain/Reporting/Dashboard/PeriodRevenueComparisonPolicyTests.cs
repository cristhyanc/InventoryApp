using Inventory.Domain.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Dashboard;

/// <summary>
/// The home Dashboard sales card's week-on-week comparison rule (issue #459). What matters here is
/// that a zero or uncovered prior period never produces a percentage: a financial figure must not be
/// shown as an infinite rise, a divide-by-zero, or a collapse in trade that is really missing data.
/// </summary>
public class PeriodRevenueComparisonPolicyTests
{
    private static readonly DateTime PriorPeriodStart = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_covered_prior_period_yields_the_amount_and_percentage_change()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            currentPeriodSales: 150m,
            priorPeriodSales: 100m,
            priorPeriodStartUtc: PriorPeriodStart,
            earliestRecordedSaleUtc: PriorPeriodStart.AddDays(-30));

        Assert.True(comparison.IsAvailable);
        Assert.Equal(100m, comparison.PriorPeriodSales);
        Assert.Equal(50m, comparison.ChangeAmount);
        Assert.Equal(50m, comparison.ChangePercent);
        Assert.Null(comparison.Note);
    }

    [Fact]
    public void A_fall_in_revenue_yields_a_negative_change()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            60m, 240m, PriorPeriodStart, PriorPeriodStart.AddDays(-1));

        Assert.Equal(-180m, comparison.ChangeAmount);
        Assert.Equal(-75m, comparison.ChangePercent);
    }

    /// <summary>
    /// Zero prior revenue is a real, known zero when the data covers the period - the comparison
    /// stays available and the amount is still meaningful - but the percentage is undefined.
    /// </summary>
    [Fact]
    public void A_zero_prior_period_keeps_the_amount_but_has_no_percentage()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            120m, 0m, PriorPeriodStart, PriorPeriodStart.AddHours(-1));

        Assert.True(comparison.IsAvailable);
        Assert.Equal(0m, comparison.PriorPeriodSales);
        Assert.Equal(120m, comparison.ChangeAmount);
        Assert.Null(comparison.ChangePercent);
        Assert.Equal(PeriodRevenueComparisonPolicy.ZeroPriorPeriodNote, comparison.Note);
    }

    [Fact]
    public void Two_zero_periods_still_have_no_percentage()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            0m, 0m, PriorPeriodStart, PriorPeriodStart);

        Assert.True(comparison.IsAvailable);
        Assert.Equal(0m, comparison.ChangeAmount);
        Assert.Null(comparison.ChangePercent);
    }

    /// <summary>
    /// Recorded sales that begin after the prior period started cannot be compared against: the
    /// prior period's zero or part-period total is missing data, not a quiet week.
    /// </summary>
    [Fact]
    public void A_prior_period_the_recorded_sales_do_not_reach_back_to_is_unavailable()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            500m, 0m, PriorPeriodStart, PriorPeriodStart.AddMilliseconds(1));

        Assert.False(comparison.IsAvailable);
        Assert.Null(comparison.PriorPeriodSales);
        Assert.Null(comparison.ChangeAmount);
        Assert.Null(comparison.ChangePercent);
        Assert.Equal(PeriodRevenueComparisonPolicy.PriorPeriodNotCoveredNote, comparison.Note);
    }

    [Fact]
    public void A_business_with_no_recorded_sales_at_all_has_no_comparison()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            0m, 0m, PriorPeriodStart, earliestRecordedSaleUtc: null);

        Assert.False(comparison.IsAvailable);
        Assert.Null(comparison.ChangeAmount);
        Assert.Equal(PeriodRevenueComparisonPolicy.PriorPeriodNotCoveredNote, comparison.Note);
    }

    /// <summary>
    /// The boundary is inclusive: the earliest recorded sale landing exactly on the prior period's
    /// first instant still covers it.
    /// </summary>
    [Fact]
    public void A_sale_recorded_exactly_at_the_prior_period_start_covers_it()
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            10m, 20m, PriorPeriodStart, PriorPeriodStart);

        Assert.True(comparison.IsAvailable);
        Assert.Equal(-50m, comparison.ChangePercent);
    }
}
