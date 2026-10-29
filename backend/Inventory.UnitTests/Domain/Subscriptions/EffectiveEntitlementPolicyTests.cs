using Inventory.Domain.Subscriptions;
using Xunit;

namespace Inventory.UnitTests.Domain.Subscriptions;

/// <summary>
/// The single effective-entitlement rule of issue #966, row by row.
///
/// <para>This is the table the acceptance criteria ask for: the rollout switch off and on, the
/// grandfathering gate, a manual grant alone, a paid plan alone, both together in either order, the
/// half-open period boundaries at the exact instant, and every way a record can fail to entitle
/// anything - expired, not started, cancelled, missing, malformed and undeclared. Each of these is a
/// case where getting it wrong either withholds a feature a business is paying for or hands over one
/// it is not, so the cases are the combinations rather than the happy path with variations.</para>
/// </summary>
public class EffectiveEntitlementPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime LastMonth = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextMonth = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    #region The rollout switch

    /// <summary>
    /// The shipped state, and the one that matters most: with the setting off nothing is enforced,
    /// whatever either record says. A business with no records and a business with an expired
    /// everything are both answered the same way - unrestricted, with no plan reported at all.
    /// </summary>
    [Fact]
    public void With_enforcement_off_every_business_is_unrestricted_whatever_its_records_say()
    {
        var entitlement = EffectiveEntitlementPolicy.Decide(
            enforcementEnabled: false,
            grandfatheringCompleted: true,
            [Grant(SubscriptionPlan.Pro, LastMonth, null)],
            Subscription(SubscriptionPlan.Starter, LastMonth, NextMonth),
            Now);

        Assert.Equal(
            new EffectiveEntitlement(
                Enforced: false,
                Plan: null,
                EntitlementSource.EnforcementDisabled,
                EndsAtUtc: null),
            entitlement);
    }

    [Fact]
    public void With_enforcement_off_a_business_with_no_record_at_all_is_unrestricted_too()
    {
        var entitlement = EffectiveEntitlementPolicy.Decide(
            enforcementEnabled: false,
            grandfatheringCompleted: false,
            [],
            billingSubscription: null,
            Now);

        Assert.False(entitlement.Enforced);
        Assert.Null(entitlement.Plan);
        Assert.Equal(EntitlementSource.EnforcementDisabled, entitlement.Source);
    }

    #endregion

    #region Enforcement cannot start before grandfathering has

    /// <summary>
    /// The criterion "enforcement cannot start early": with the setting on but no completion marker,
    /// every business is unrestricted - including one that would otherwise be on Free, which is
    /// exactly the business this rule protects.
    /// </summary>
    [Fact]
    public void With_enforcement_on_and_grandfathering_incomplete_every_business_is_unrestricted()
    {
        var entitlement = EffectiveEntitlementPolicy.Decide(
            enforcementEnabled: true,
            grandfatheringCompleted: false,
            [],
            billingSubscription: null,
            Now);

        Assert.Equal(
            new EffectiveEntitlement(
                Enforced: false,
                Plan: null,
                EntitlementSource.GrandfatheringNotCompleted,
                EndsAtUtc: null),
            entitlement);
    }

    /// <summary>And a business that holds records is not restricted by them either while the step is unfinished.</summary>
    [Fact]
    public void An_incomplete_grandfathering_step_outranks_both_records()
    {
        var entitlement = EffectiveEntitlementPolicy.Decide(
            enforcementEnabled: true,
            grandfatheringCompleted: false,
            [Grant(SubscriptionPlan.Free, LastMonth, null)],
            Subscription(SubscriptionPlan.Free, LastMonth, NextMonth),
            Now);

        Assert.False(entitlement.Enforced);
        Assert.Null(entitlement.Plan);
        Assert.Equal(EntitlementSource.GrandfatheringNotCompleted, entitlement.Source);
    }

    #endregion

    #region Nothing active means Free

    [Fact]
    public void With_no_record_at_all_the_business_is_on_free()
    {
        var entitlement = Enforcing([], null);

        Assert.Equal(
            new EffectiveEntitlement(
                Enforced: true,
                SubscriptionPlan.Free,
                EntitlementSource.NoActiveRecord,
                EndsAtUtc: null),
            entitlement);
    }

    [Fact]
    public void An_expired_grant_leaves_the_business_on_free()
    {
        var entitlement = Enforcing([Grant(SubscriptionPlan.Pro, LastMonth, Now.AddDays(-1))], null);

        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
        Assert.Equal(EntitlementSource.NoActiveRecord, entitlement.Source);
    }

    [Fact]
    public void A_grant_that_has_not_started_leaves_the_business_on_free()
    {
        var entitlement = Enforcing([Grant(SubscriptionPlan.Pro, Now.AddDays(1), null)], null);

        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
    }

    [Fact]
    public void A_cancelled_subscription_leaves_the_business_on_free_inside_its_period()
    {
        var entitlement = Enforcing(
            [],
            Subscription(SubscriptionPlan.Pro, LastMonth, NextMonth, BillingSubscriptionStatus.Cancelled));

        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
        Assert.Equal(EntitlementSource.NoActiveRecord, entitlement.Source);
    }

    [Fact]
    public void An_expired_subscription_leaves_the_business_on_free()
    {
        var entitlement = Enforcing([], Subscription(SubscriptionPlan.Pro, LastMonth, Now));

        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
    }

    /// <summary>
    /// A status value this code does not declare entitles nothing. It is the fail-closed direction
    /// for a paid feature, and the state an older deployment reading a newer one's row is in.
    /// </summary>
    [Fact]
    public void A_subscription_whose_status_is_not_declared_entitles_nothing()
    {
        var entitlement = Enforcing(
            [],
            Subscription(SubscriptionPlan.Pro, LastMonth, NextMonth, (BillingSubscriptionStatus)0));

        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
        Assert.Equal(EntitlementSource.NoActiveRecord, entitlement.Source);
    }

    /// <summary>And a plan value it does not declare is a record it cannot read, not the nearest plan it knows.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void A_record_whose_plan_is_not_declared_entitles_nothing(int undeclared)
    {
        var plan = (SubscriptionPlan)undeclared;

        Assert.Equal(SubscriptionPlan.Free, Enforcing([Grant(plan, LastMonth, null)], null).Plan);
        Assert.Equal(
            SubscriptionPlan.Free,
            Enforcing([], Subscription(plan, LastMonth, NextMonth)).Plan);
    }

    /// <summary>
    /// A period whose end is at or before its start entitles nothing at any instant, so it is
    /// ignored rather than read as open-ended - which is what treating a malformed record as "no
    /// recorded end" would do.
    /// </summary>
    [Fact]
    public void A_record_whose_period_ends_before_it_starts_entitles_nothing()
    {
        var entitlement = Enforcing([Grant(SubscriptionPlan.Pro, LastMonth, LastMonth)], null);

        Assert.Equal(SubscriptionPlan.Free, entitlement.Plan);
        Assert.Equal(EntitlementSource.NoActiveRecord, entitlement.Source);
    }

    #endregion

    #region One record

    [Fact]
    public void An_open_ended_grant_is_the_effective_plan_with_no_end_reported()
    {
        var entitlement = Enforcing([Grant(SubscriptionPlan.Pro, LastMonth, null)], null);

        Assert.Equal(
            new EffectiveEntitlement(
                Enforced: true,
                SubscriptionPlan.Pro,
                EntitlementSource.ManualGrant,
                EndsAtUtc: null),
            entitlement);
    }

    [Fact]
    public void A_complimentary_grant_with_an_end_date_reports_that_end()
    {
        var entitlement = Enforcing(
            [Grant(SubscriptionPlan.Starter, LastMonth, NextMonth, ManualPlanGrantSource.Complimentary)],
            null);

        Assert.Equal(
            new EffectiveEntitlement(
                Enforced: true,
                SubscriptionPlan.Starter,
                EntitlementSource.ManualGrant,
                NextMonth),
            entitlement);
    }

    [Fact]
    public void An_active_paid_plan_is_the_effective_plan_and_reports_its_period_end()
    {
        var entitlement = Enforcing([], Subscription(SubscriptionPlan.Starter, LastMonth, NextMonth));

        Assert.Equal(
            new EffectiveEntitlement(
                Enforced: true,
                SubscriptionPlan.Starter,
                EntitlementSource.BillingSubscription,
                NextMonth),
            entitlement);
    }

    #endregion

    #region The higher of the two

    [Fact]
    public void The_paid_plan_wins_when_it_is_the_higher_one()
    {
        var entitlement = Enforcing(
            [Grant(SubscriptionPlan.Starter, LastMonth, null)],
            Subscription(SubscriptionPlan.Pro, LastMonth, NextMonth));

        Assert.Equal(SubscriptionPlan.Pro, entitlement.Plan);
        Assert.Equal(EntitlementSource.BillingSubscription, entitlement.Source);
        Assert.Equal(NextMonth, entitlement.EndsAtUtc);
    }

    [Fact]
    public void The_grant_wins_when_it_is_the_higher_one()
    {
        var entitlement = Enforcing(
            [Grant(SubscriptionPlan.Pro, LastMonth, null)],
            Subscription(SubscriptionPlan.Starter, LastMonth, NextMonth));

        Assert.Equal(SubscriptionPlan.Pro, entitlement.Plan);
        Assert.Equal(EntitlementSource.ManualGrant, entitlement.Source);
        Assert.Null(entitlement.EndsAtUtc);
    }

    /// <summary>
    /// An expired grant does not drag a paying business down, and a cancelled subscription does not
    /// take a grant away: "the higher of the <em>active</em> records" is two independent questions.
    /// </summary>
    [Fact]
    public void An_inactive_record_never_lowers_the_plan_the_other_one_entitles()
    {
        Assert.Equal(
            SubscriptionPlan.Pro,
            Enforcing(
                [Grant(SubscriptionPlan.Free, LastMonth, Now.AddDays(-1))],
                Subscription(SubscriptionPlan.Pro, LastMonth, NextMonth)).Plan);

        Assert.Equal(
            SubscriptionPlan.Pro,
            Enforcing(
                [Grant(SubscriptionPlan.Pro, LastMonth, null)],
                Subscription(
                    SubscriptionPlan.Free,
                    LastMonth,
                    NextMonth,
                    BillingSubscriptionStatus.Cancelled)).Plan);
    }

    /// <summary>
    /// Equal plans: the record that entitles for longer is the one reported, so the reported end
    /// instant is the truth about when the plan is lost. A grandfathered Pro grant with no end date
    /// beside a Pro subscription ending next month keeps Pro after next month.
    /// </summary>
    [Fact]
    public void With_equal_plans_the_record_that_entitles_for_longer_is_reported()
    {
        var entitlement = Enforcing(
            [Grant(SubscriptionPlan.Pro, LastMonth, null)],
            Subscription(SubscriptionPlan.Pro, LastMonth, NextMonth));

        Assert.Equal(EntitlementSource.ManualGrant, entitlement.Source);
        Assert.Null(entitlement.EndsAtUtc);
    }

    [Fact]
    public void With_equal_plans_an_open_ended_subscription_outlasts_a_grant_that_ends()
    {
        var entitlement = Enforcing(
            [Grant(SubscriptionPlan.Pro, LastMonth, NextMonth)],
            Subscription(SubscriptionPlan.Pro, LastMonth, null));

        Assert.Equal(EntitlementSource.BillingSubscription, entitlement.Source);
        Assert.Null(entitlement.EndsAtUtc);
    }

    /// <summary>A genuine tie reports the record the business is paying for.</summary>
    [Fact]
    public void With_equal_plans_and_equal_ends_the_paid_subscription_is_reported()
    {
        var entitlement = Enforcing(
            [Grant(SubscriptionPlan.Pro, LastMonth, NextMonth)],
            Subscription(SubscriptionPlan.Pro, LastMonth, NextMonth));

        Assert.Equal(EntitlementSource.BillingSubscription, entitlement.Source);
        Assert.Equal(NextMonth, entitlement.EndsAtUtc);
    }

    /// <summary>
    /// Several grants: the highest active plan decides, and between two of that plan the longer one
    /// does - so a short complimentary grant cannot shorten an open-ended grandfathered one.
    /// </summary>
    [Fact]
    public void The_highest_and_longest_active_grant_decides()
    {
        var entitlement = Enforcing(
            [
                Grant(SubscriptionPlan.Starter, LastMonth, NextMonth, ManualPlanGrantSource.Complimentary),
                Grant(SubscriptionPlan.Pro, LastMonth, null),
                Grant(SubscriptionPlan.Pro, LastMonth, NextMonth, ManualPlanGrantSource.Complimentary),
            ],
            null);

        Assert.Equal(SubscriptionPlan.Pro, entitlement.Plan);
        Assert.Null(entitlement.EndsAtUtc);
    }

    #endregion

    #region The half-open period boundaries

    /// <summary>
    /// The start instant is inclusive: a grant beginning exactly now entitles now. Asserted at the
    /// instant itself, because an off-by-one here is a business told it has nothing on the first
    /// second of the plan it was given.
    /// </summary>
    [Fact]
    public void A_period_starting_at_this_exact_instant_already_entitles()
    {
        Assert.Equal(
            SubscriptionPlan.Pro,
            Enforcing([Grant(SubscriptionPlan.Pro, Now, null)], null).Plan);

        Assert.Equal(
            SubscriptionPlan.Starter,
            Enforcing([], Subscription(SubscriptionPlan.Starter, Now, NextMonth)).Plan);
    }

    /// <summary>
    /// The end instant is exclusive: at the instant it ends, it is over. That is what makes a
    /// renewal's new period neither overlap the old one nor leave a gap.
    /// </summary>
    [Fact]
    public void A_period_ending_at_this_exact_instant_no_longer_entitles()
    {
        Assert.Equal(
            SubscriptionPlan.Free,
            Enforcing([Grant(SubscriptionPlan.Pro, LastMonth, Now)], null).Plan);

        Assert.Equal(
            SubscriptionPlan.Free,
            Enforcing([], Subscription(SubscriptionPlan.Pro, LastMonth, Now)).Plan);
    }

    /// <summary>One tick before the end it still does, which is the other half of the same boundary.</summary>
    [Fact]
    public void A_period_ending_one_tick_from_now_still_entitles()
    {
        Assert.Equal(
            SubscriptionPlan.Pro,
            Enforcing([Grant(SubscriptionPlan.Pro, LastMonth, Now.AddTicks(1))], null).Plan);
    }

    #endregion

    [Fact]
    public void A_null_grant_list_is_refused_rather_than_read_as_no_grants() =>
        Assert.Throws<ArgumentNullException>(() => EffectiveEntitlementPolicy.Decide(
            enforcementEnabled: true,
            grandfatheringCompleted: true,
            manualGrants: null!,
            billingSubscription: null,
            Now));

    private static EffectiveEntitlement Enforcing(
        ManualPlanGrantState[] grants,
        BillingSubscriptionState? subscription) =>
        EffectiveEntitlementPolicy.Decide(
            enforcementEnabled: true,
            grandfatheringCompleted: true,
            grants,
            subscription,
            Now);

    private static ManualPlanGrantState Grant(
        SubscriptionPlan plan,
        DateTime startsAtUtc,
        DateTime? endsAtUtc,
        ManualPlanGrantSource source = ManualPlanGrantSource.Grandfathered) =>
        new(plan, source, new EntitlementPeriod(startsAtUtc, endsAtUtc));

    private static BillingSubscriptionState Subscription(
        SubscriptionPlan plan,
        DateTime startsAtUtc,
        DateTime? endsAtUtc,
        BillingSubscriptionStatus status = BillingSubscriptionStatus.Active) =>
        new(plan, status, new EntitlementPeriod(startsAtUtc, endsAtUtc));
}
