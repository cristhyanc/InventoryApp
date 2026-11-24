using Inventory.Domain.Maintenance;
using Xunit;

namespace InventoryApi.Tests.Domain.Maintenance;

/// <summary>
/// When the application collects the sales date-range index's SQLite planner statistics, and - which
/// is the point of the rule - when it does not (issue #1287, the repository owner's decision of
/// 24 November 2026).
///
/// <para>The boundaries are what make the refresh bounded, so every one of them is asserted: the
/// floor below which an empty or barely-used database collects nothing, and the growth factor either
/// side of which a table is or is not still described by what was recorded.</para>
/// </summary>
public class SalesDateRangeStatisticsPolicyTests
{
    /// <summary>
    /// A brand-new database: no statistics and no sales. Nothing is collected, which is what keeps
    /// the write lock untaken at every startup until the business has actually traded.
    /// </summary>
    [Fact]
    public void An_empty_sales_table_with_no_statistics_collects_nothing()
    {
        Assert.Equal(
            SalesDateRangeStatisticsDecision.NotWorthCollecting,
            SalesDateRangeStatisticsPolicy.Decide(recordedSales: null, currentSales: 0));
    }

    /// <summary>
    /// The floor itself: one sale short of it collects nothing, and the floor collects. SQLite only
    /// considers the skip-scan these statistics exist to enable when a distinct business is worth far
    /// more than a handful of rows, so below the floor there is nothing to buy.
    /// </summary>
    [Theory]
    [InlineData(SalesDateRangeStatisticsPolicy.MinimumSales - 1, SalesDateRangeStatisticsDecision.NotWorthCollecting)]
    [InlineData(SalesDateRangeStatisticsPolicy.MinimumSales, SalesDateRangeStatisticsDecision.Collect)]
    [InlineData(SalesDateRangeStatisticsPolicy.MinimumSales + 1, SalesDateRangeStatisticsDecision.Collect)]
    public void The_floor_decides_a_database_that_has_no_statistics_at_all(
        long currentSales, SalesDateRangeStatisticsDecision expected)
    {
        Assert.Equal(expected, SalesDateRangeStatisticsPolicy.Decide(null, currentSales));
    }

    /// <summary>
    /// SQLite records no statistics row for an empty index, so a recorded count of zero - or a
    /// negative one, which nothing should ever write - is read as "none recorded" rather than as an
    /// honest zero that every later comparison would then be made against.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_recorded_count_of_zero_or_less_is_read_as_no_statistics(long recordedSales)
    {
        Assert.Equal(
            SalesDateRangeStatisticsDecision.Collect,
            SalesDateRangeStatisticsPolicy.Decide(recordedSales, SalesDateRangeStatisticsPolicy.MinimumSales));
        Assert.Equal(
            SalesDateRangeStatisticsDecision.NotWorthCollecting,
            SalesDateRangeStatisticsPolicy.Decide(recordedSales, SalesDateRangeStatisticsPolicy.MinimumSales - 1));
    }

    /// <summary>
    /// The growth bound: a table that has grown by less than the factor is still described by what
    /// was recorded and collects nothing; at the factor it collects again. This is what makes the
    /// number of collections about <c>log₄(sales)</c> over the life of a database rather than one per
    /// import or per restart.
    /// </summary>
    [Theory]
    [InlineData(10_000L, 10_000L, SalesDateRangeStatisticsDecision.AlreadyCurrent)]
    [InlineData(10_000L, 39_999L, SalesDateRangeStatisticsDecision.AlreadyCurrent)]
    [InlineData(10_000L, 40_000L, SalesDateRangeStatisticsDecision.Collect)]
    [InlineData(10_000L, 250_000L, SalesDateRangeStatisticsDecision.Collect)]
    public void Growth_past_the_factor_collects_again(
        long recordedSales, long currentSales, SalesDateRangeStatisticsDecision expected)
    {
        Assert.Equal(expected, SalesDateRangeStatisticsPolicy.Decide(recordedSales, currentSales));
    }

    /// <summary>
    /// And the same bound the other way. What is being kept is a description of the table rather than
    /// a high-water mark, so statistics that describe four times the rows the table now holds are
    /// collected again too.
    /// </summary>
    [Theory]
    [InlineData(40_000L, 10_001L, SalesDateRangeStatisticsDecision.AlreadyCurrent)]
    [InlineData(40_000L, 10_000L, SalesDateRangeStatisticsDecision.Collect)]
    [InlineData(40_000L, 0L, SalesDateRangeStatisticsDecision.Collect)]
    public void Shrinking_past_the_factor_collects_again(
        long recordedSales, long currentSales, SalesDateRangeStatisticsDecision expected)
    {
        Assert.Equal(expected, SalesDateRangeStatisticsPolicy.Decide(recordedSales, currentSales));
    }

    /// <summary>
    /// The floor applies only to a database with no statistics. Once they exist they are kept
    /// current, because a table that is small <em>now</em> has recorded statistics that say otherwise
    /// and nothing else would ever correct them.
    /// </summary>
    [Fact]
    public void Existing_statistics_are_kept_current_even_below_the_floor()
    {
        Assert.Equal(
            SalesDateRangeStatisticsDecision.AlreadyCurrent,
            SalesDateRangeStatisticsPolicy.Decide(recordedSales: 100, currentSales: 50));
        Assert.Equal(
            SalesDateRangeStatisticsDecision.Collect,
            SalesDateRangeStatisticsPolicy.Decide(recordedSales: 100, currentSales: 25));
    }

    /// <summary>
    /// The numbers themselves, asserted so that changing one is a deliberate change to a documented
    /// bound rather than a quiet tuning.
    /// </summary>
    [Fact]
    public void The_bounds_are_the_documented_ones()
    {
        Assert.Equal(1_000, SalesDateRangeStatisticsPolicy.MinimumSales);
        Assert.Equal(4, SalesDateRangeStatisticsPolicy.GrowthFactor);
    }
}
