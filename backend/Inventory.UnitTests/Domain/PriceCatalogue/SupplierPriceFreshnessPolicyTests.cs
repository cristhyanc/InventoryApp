using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// How old an observed supplier price may be and still be compared (issue #925): the owner's two
/// limits, the margin they are built from, and the boundary itself.
/// </summary>
public class SupplierPriceFreshnessPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The two decided limits are the check interval plus the margin, stated as that arithmetic
    /// rather than as two bare numbers - so a reader can see why 30 and 102 are the answers and a
    /// third cadence is derived the same way.
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

        var observedAt = new DateTime(2026, 11, 5, 21, 0, 0, DateTimeKind.Utc);
        Assert.True(SupplierPriceFreshnessPolicy.IsFresh(
            observedAt, observedAt.AddHours(173), SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours));
        Assert.False(SupplierPriceFreshnessPolicy.IsFresh(
            observedAt, observedAt.AddHours(174), SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours));
    }

    /// <summary>
    /// The limit is the age at which a price stops being usable: fresh just under it, stale at it.
    /// The boundary belongs to one side deliberately rather than by accident.
    /// </summary>
    [Theory]
    [InlineData(29.99, true)]
    [InlineData(30.0, false)]
    [InlineData(30.01, false)]
    public void A_price_is_fresh_up_to_its_limit_and_stale_at_it(double ageHours, bool fresh) =>
        Assert.Equal(
            fresh,
            SupplierPriceFreshnessPolicy.IsFresh(
                Now.AddHours(-ageHours),
                Now,
                SupplierPriceFreshnessPolicy.DailyCheckLimitHours));

    /// <summary>
    /// The margin is what the limits exist for: a run that starts six hours later than yesterday's
    /// finds yesterday's daily prices still current, which is precisely the case that would otherwise
    /// mark every price stale.
    /// </summary>
    [Fact]
    public void A_run_that_starts_later_than_yesterdays_still_finds_yesterdays_prices_fresh()
    {
        var observedYesterday = Now.AddHours(-(SupplierPriceFreshnessPolicy.DailyCheckIntervalHours
            + SupplierPriceFreshnessPolicy.MarginHours - 1));

        Assert.True(
            SupplierPriceFreshnessPolicy.IsFresh(
                observedYesterday,
                Now,
                SupplierPriceFreshnessPolicy.DailyCheckLimitHours));
    }

    /// <summary>
    /// A twice-weekly supplier's four-day gap is covered, and the same price is long stale for a
    /// daily one - the whole reason the limit travels with the supplier rather than with this file.
    /// </summary>
    [Fact]
    public void One_price_can_be_fresh_for_one_supplier_and_stale_for_another()
    {
        var observed = Now.AddHours(-90);

        Assert.True(
            SupplierPriceFreshnessPolicy.IsFresh(
                observed,
                Now,
                SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours));
        Assert.False(
            SupplierPriceFreshnessPolicy.IsFresh(
                observed,
                Now,
                SupplierPriceFreshnessPolicy.DailyCheckLimitHours));
    }

    /// <summary>
    /// An instant in the future - two clocks that disagree - is fresh. Calling a price nobody has had
    /// time to age "too old" would be the one answer that is certainly wrong.
    /// </summary>
    [Fact]
    public void A_price_observed_in_the_future_is_fresh() =>
        Assert.True(
            SupplierPriceFreshnessPolicy.IsFresh(
                Now.AddHours(1),
                Now,
                SupplierPriceFreshnessPolicy.DailyCheckLimitHours));

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

    /// <summary>
    /// A staleness question asked with a limit nobody accepted is refused rather than answered: the
    /// freshness rule has no behaviour at all for a limit that is not a limit.
    /// </summary>
    [Fact]
    public void Freshness_cannot_be_decided_under_an_unaccepted_limit() =>
        Assert.Throws<DomainValidationException>(
            () => SupplierPriceFreshnessPolicy.IsFresh(Now.AddHours(-1), Now, 0));
}
