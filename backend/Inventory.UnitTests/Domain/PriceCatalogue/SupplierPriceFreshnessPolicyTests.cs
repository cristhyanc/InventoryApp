using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// How old an observed supplier price may be and still be compared: the schedule-based boundary of
/// issue #1200, its margin, and the stored hours limit that now only describes a cadence (#925).
/// </summary>
public class SupplierPriceFreshnessPolicyTests
{
    /// <summary>
    /// The end of the next scheduled search day, as the collection calendar converts it: 00:00 Sydney
    /// on Thursday 12 November 2026 is 13:00 UTC on the Wednesday.
    /// </summary>
    private static readonly DateTime NextSearchDayEnd =
        new(2026, 11, 11, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The boundary is the end of the day the next search should have happened on, plus the margin:
    /// 06:00 Sydney the morning after, which is what the owner asked for on 12 November 2026.
    /// </summary>
    [Fact]
    public void A_price_is_comparable_until_the_next_search_day_ends_plus_the_margin()
    {
        Assert.Equal(6, SupplierPriceFreshnessPolicy.MarginHours);

        Assert.Equal(
            NextSearchDayEnd.AddHours(SupplierPriceFreshnessPolicy.MarginHours),
            SupplierPriceFreshnessPolicy.ComparableUntilUtc(NextSearchDayEnd));
    }

    /// <summary>
    /// Fresh one tick before the boundary and stale at it: the boundary belongs to the stale side
    /// deliberately rather than by accident, which is the rule the hours limit it replaced had.
    /// </summary>
    [Fact]
    public void The_boundary_itself_belongs_to_the_stale_side()
    {
        var boundary = SupplierPriceFreshnessPolicy.ComparableUntilUtc(NextSearchDayEnd);

        Assert.True(SupplierPriceFreshnessPolicy.IsFreshUntil(
            boundary.AddTicks(-1), NextSearchDayEnd));
        Assert.False(SupplierPriceFreshnessPolicy.IsFreshUntil(boundary, NextSearchDayEnd));
        Assert.False(SupplierPriceFreshnessPolicy.IsFreshUntil(
            boundary.AddHours(1), NextSearchDayEnd));
    }

    /// <summary>
    /// The whole of the next search day is inside the window, which is the case the margin exists
    /// for: a run that starts in the evening instead of the morning has not made yesterday's price
    /// wrong, and a page of uncomparable offers is worse guidance than a price that is a day old.
    /// </summary>
    [Fact]
    public void The_whole_of_the_next_search_day_is_still_comparable()
    {
        Assert.True(SupplierPriceFreshnessPolicy.IsFreshUntil(
            NextSearchDayEnd.AddHours(-24), NextSearchDayEnd));
        Assert.True(SupplierPriceFreshnessPolicy.IsFreshUntil(
            NextSearchDayEnd.AddTicks(-1), NextSearchDayEnd));
        Assert.True(SupplierPriceFreshnessPolicy.IsFreshUntil(NextSearchDayEnd, NextSearchDayEnd));
    }

    /// <summary>
    /// A price whose next search day is still ahead - including a reading from a clock that
    /// disagrees with itself, whose own day is in the future - is fresh. Calling a price nobody has
    /// had time to age "too old" would be the one answer that is certainly wrong.
    /// </summary>
    [Fact]
    public void A_price_whose_next_search_day_is_still_ahead_is_fresh() =>
        Assert.True(SupplierPriceFreshnessPolicy.IsFreshUntil(
            NextSearchDayEnd.AddDays(-3), NextSearchDayEnd));

    /// <summary>
    /// The two decided limits are the check interval plus the margin, stated as that arithmetic
    /// rather than as two bare numbers - so a reader can see why 30 and 102 are the answers and a
    /// third cadence is derived the same way. Since issue #1200 they describe a cadence and decide
    /// nothing: what a supplier's stored <c>PriceCheckFreshnessHours</c> is derived from.
    /// </summary>
    [Fact]
    public void The_two_decided_limits_are_their_check_interval_plus_the_margin()
    {
        Assert.Equal(30, SupplierPriceFreshnessPolicy.DailyCheckLimitHours);
        Assert.Equal(102, SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours);

        Assert.Equal(
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            SupplierPriceFreshnessPolicy.LimitForCheckInterval(
                SupplierPriceFreshnessPolicy.DailyCheckIntervalHours));
        Assert.Equal(
            SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours,
            SupplierPriceFreshnessPolicy.LimitForCheckInterval(
                SupplierPriceFreshnessPolicy.TwiceWeeklyCheckIntervalHours));
    }

    /// <summary>
    /// The weekly limit (owner decision, 1 November 2026: BIG W on Thursdays, Kmart on Mondays) is
    /// derived the same way: a week plus the margin, 174 hours.
    /// </summary>
    [Fact]
    public void The_weekly_limit_is_a_week_plus_the_margin()
    {
        Assert.Equal(174, SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours);
        Assert.Equal(
            SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours,
            SupplierPriceFreshnessPolicy.LimitForCheckInterval(
                SupplierPriceFreshnessPolicy.WeeklyCheckIntervalHours));
    }

    /// <summary>
    /// Zero is refused rather than read as "never stale" or "always stale": an absent limit would be
    /// a silently wrong answer in whichever direction somebody resolved it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SupplierPriceFreshnessPolicy.MaxLimitHours + 1)]
    public void A_limit_outside_the_accepted_range_is_refused(int limitHours)
    {
        Assert.False(SupplierPriceFreshnessPolicy.IsAcceptedLimit(limitHours));

        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierPriceFreshnessPolicy.RequireLimit(limitHours));

        Assert.Equal(SupplierPriceFreshnessPolicy.LimitOutOfRangeMessage, refusal.Message);
    }

    /// <summary>Both decided limits, and the bounds themselves, are accepted limits.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(SupplierPriceFreshnessPolicy.DailyCheckLimitHours)]
    [InlineData(SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours)]
    [InlineData(SupplierPriceFreshnessPolicy.MaxLimitHours)]
    public void An_accepted_limit_is_stored_as_it_is(int limitHours) =>
        Assert.Equal(limitHours, SupplierPriceFreshnessPolicy.RequireLimit(limitHours));

    /// <summary>A check interval that is not positive describes no cadence and is refused.</summary>
    [Fact]
    public void A_check_interval_that_is_not_positive_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => SupplierPriceFreshnessPolicy.LimitForCheckInterval(0));
}
