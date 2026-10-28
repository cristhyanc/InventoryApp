using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// What one collector run is asked to do (issue #924): which suppliers are due, and how much work a
/// run may be handed.
///
/// <para>The cadence rules are the interesting half. A supplier's check interval is read from its own
/// stored freshness limit, so nothing here names a supplier - and the two shipped cadences the owner
/// decided on 27 October 2026 (daily, and BIG W's twice-weekly) are exercised through those stored
/// numbers rather than through a supplier's identity.</para>
/// </summary>
public class PriceCollectorWorkListPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

    #region Which suppliers are due

    /// <summary>
    /// A supplier that has never been checked successfully is due. That is every supplier the first
    /// time the collector runs, and a supplier added to the platform later.
    /// </summary>
    [Fact]
    public void A_supplier_that_has_never_been_checked_is_due() =>
        Assert.True(PriceCollectorWorkListPolicy.IsSupplierDue(
            isEnabled: true,
            hasConnector: true,
            lastCheckedAtUtc: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Now));

    /// <summary>
    /// A supplier nothing collects is never due, whatever its cadence says and however long ago it
    /// was checked. Costco is in that state and it is a real one: a route that cannot be taken is not
    /// work, and issuing it would be a work item no result could ever answer.
    /// </summary>
    [Fact]
    public void A_supplier_with_no_connector_is_never_due() =>
        Assert.False(PriceCollectorWorkListPolicy.IsSupplierDue(
            isEnabled: true,
            hasConnector: false,
            lastCheckedAtUtc: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Now));

    /// <summary>
    /// A disabled supplier is never due either. Disabling is how the platform stops suggesting a
    /// supplier, and a supplier it has stopped suggesting is not one to keep searching.
    /// </summary>
    [Fact]
    public void A_disabled_supplier_is_never_due() =>
        Assert.False(PriceCollectorWorkListPolicy.IsSupplierDue(
            isEnabled: false,
            hasConnector: true,
            lastCheckedAtUtc: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Now));

    /// <summary>
    /// The cadence boundary for a daily supplier: due at twenty-four hours, and not before.
    ///
    /// <para>Twenty-four rather than thirty is the point of the test. Thirty is the <em>freshness</em>
    /// limit - the age at which a price stops being comparable - and it is the interval plus the
    /// six-hour margin. Deriving dueness from the limit itself would let a daily supplier's prices
    /// reach twenty-nine hours old before anything went to look at them, which is what the margin
    /// exists to avoid rather than to cause.</para>
    /// </summary>
    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    public void A_daily_supplier_is_due_once_its_own_interval_has_elapsed(int hoursSince, bool isDue) =>
        Assert.Equal(
            isDue,
            PriceCollectorWorkListPolicy.IsSupplierDue(
                isEnabled: true,
                hasConnector: true,
                Now.AddHours(-hoursSince),
                SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
                Now));

    /// <summary>
    /// And the twice-weekly cadence, from the same stored number: due at ninety-six hours, which is
    /// the Sunday-to-Thursday gap the owner's 102-hour limit was derived from.
    /// </summary>
    [Theory]
    [InlineData(95, false)]
    [InlineData(96, true)]
    public void A_twice_weekly_supplier_is_due_once_its_own_interval_has_elapsed(int hoursSince, bool isDue) =>
        Assert.Equal(
            isDue,
            PriceCollectorWorkListPolicy.IsSupplierDue(
                isEnabled: true,
                hasConnector: true,
                Now.AddHours(-hoursSince),
                SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours,
                Now));

    /// <summary>
    /// A last-check instant in the future - a clock that disagrees with itself - leaves the supplier
    /// not due. The alternative is to re-search something that was just searched, which is the
    /// opposite of what a cadence is for.
    /// </summary>
    [Fact]
    public void A_last_check_in_the_future_leaves_a_supplier_not_due() =>
        Assert.False(PriceCollectorWorkListPolicy.IsSupplierDue(
            isEnabled: true,
            hasConnector: true,
            Now.AddHours(1),
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Now));

    /// <summary>
    /// A stored limit at or below the six-hour margin inverts to a non-positive interval, which is a
    /// supplier that is always due. It is answered rather than refused: "check it every time" is a
    /// coherent cadence, and throwing at read time would take the whole work list down over one
    /// supplier's configuration.
    /// </summary>
    [Theory]
    [InlineData(SupplierPriceFreshnessPolicy.MarginHours)]
    [InlineData(1)]
    public void A_limit_inside_the_margin_makes_a_supplier_due_every_run(int limitHours)
    {
        Assert.Equal(0, PriceCollectorWorkListPolicy.CheckIntervalForLimit(limitHours));
        Assert.True(PriceCollectorWorkListPolicy.IsSupplierDue(
            isEnabled: true,
            hasConnector: true,
            Now,
            limitHours,
            Now));
    }

    /// <summary>
    /// The interval is the exact inverse of the freshness limit's own derivation, which is what keeps
    /// one stored number answering both questions.
    /// </summary>
    [Theory]
    [InlineData(SupplierPriceFreshnessPolicy.DailyCheckIntervalHours)]
    [InlineData(SupplierPriceFreshnessPolicy.TwiceWeeklyCheckIntervalHours)]
    public void The_interval_inverts_the_freshness_limits_own_derivation(int interval) =>
        Assert.Equal(
            interval,
            PriceCollectorWorkListPolicy.CheckIntervalForLimit(
                SupplierPriceFreshnessPolicy.LimitForCheckInterval(interval)));

    #endregion

    #region How much work a run may carry

    /// <summary>
    /// The budget is the configured one while it is inside the reviewed maximum, and the maximum
    /// otherwise: no setting may widen the response past the bound this policy states.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(PriceCollectorWorkListPolicy.MaxWorkItemsPerRun, PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)]
    [InlineData(PriceCollectorWorkListPolicy.MaxWorkItemsPerRun + 1, PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)]
    public void The_budget_is_the_configured_one_bounded_by_the_reviewed_maximum(int configured, int expected) =>
        Assert.Equal(expected, PriceCollectorWorkListPolicy.BudgetFor(configured));

    /// <summary>
    /// A non-positive budget is refused rather than read as "no work" or "unlimited": both are
    /// silently wrong, and a run issued with no work at all is not something to arrive at by
    /// accident.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_budget_is_refused(int configured)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => PriceCollectorWorkListPolicy.BudgetFor(configured));

        Assert.Equal(PriceCollectorWorkListPolicy.NonPositiveBudgetMessage, refusal.Message);
    }

    #endregion
}
