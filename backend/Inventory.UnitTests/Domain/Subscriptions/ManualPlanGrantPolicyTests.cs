using Inventory.Domain.Subscriptions;
using Xunit;

namespace Inventory.UnitTests.Domain.Subscriptions;

/// <summary>
/// The rules a Super Admin's manual grant is decided by (issue #982): which plans may be granted at
/// all, what a reason has to be, and the one conversion between the end <em>date</em> an
/// administrator chooses and the exclusive end <em>instant</em> issue #966's half-open period is
/// stored as.
///
/// <para>Every case here is a pure function of its arguments. The clock, the actor, the revision and
/// the database belong to the use case that calls these rules, which is what makes the boundary
/// cases below answerable at the tick rather than only through HTTP.</para>
/// </summary>
public class ManualPlanGrantPolicyTests
{
    [Theory]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Pro)]
    public void Starter_and_pro_are_the_grantable_plans(SubscriptionPlan plan)
    {
        Assert.True(ManualPlanGrantPolicy.IsGrantable(plan));
        Assert.Contains(plan, ManualPlanGrantPolicy.GrantablePlans);
    }

    /// <summary>
    /// Free is a plan, and granting it is not a grant: a business with no record at all is already
    /// on Free (<see cref="EffectiveEntitlementPolicy"/>), so a "Free grant" would be a row that
    /// entitles nothing and could only confuse the trail it was written into.
    /// </summary>
    [Fact]
    public void Free_is_not_grantable()
    {
        Assert.False(ManualPlanGrantPolicy.IsGrantable(SubscriptionPlan.Free));
        Assert.DoesNotContain(SubscriptionPlan.Free, ManualPlanGrantPolicy.GrantablePlans);
    }

    /// <summary>
    /// And a value this code does not declare is refused rather than resolved to the nearest plan -
    /// the restraint <see cref="SubscriptionPlans.IsDeclared"/> exists for, applied on the way in.
    /// </summary>
    [Fact]
    public void An_undeclared_plan_is_not_grantable()
    {
        Assert.False(ManualPlanGrantPolicy.IsGrantable((SubscriptionPlan)0));
        Assert.False(ManualPlanGrantPolicy.IsGrantable((SubscriptionPlan)99));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_blank_reason_is_no_reason(string? reason)
    {
        Assert.Null(ManualPlanGrantPolicy.NormaliseReason(reason));
    }

    [Fact]
    public void A_reason_is_normalised_by_trimming_it()
    {
        Assert.Equal("Grandfathered pilot site", ManualPlanGrantPolicy.NormaliseReason("  Grandfathered pilot site  "));
    }

    [Fact]
    public void A_reason_at_the_length_limit_is_accepted_and_one_character_more_is_not()
    {
        var atTheLimit = new string('r', ManualPlanGrantPolicy.MaxReasonLength);

        Assert.False(ManualPlanGrantPolicy.IsReasonTooLong(atTheLimit));
        Assert.True(ManualPlanGrantPolicy.IsReasonTooLong(atTheLimit + "r"));
    }

    /// <summary>
    /// The one conversion: the administrator chooses the last UTC day the grant applies, and what is
    /// stored is the <em>exclusive</em> instant at the start of the next day - the half-open
    /// convention of <see cref="EntitlementPeriod"/>, so a grant ending on 31 December still covers
    /// every moment of it.
    /// </summary>
    [Fact]
    public void The_end_date_is_stored_as_the_exclusive_start_of_the_next_day()
    {
        var endsAtUtc = ManualPlanGrantPolicy.ExclusiveEndOf(new DateOnly(2026, 12, 31));

        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), endsAtUtc);
        Assert.Equal(DateTimeKind.Utc, endsAtUtc.Kind);
    }

    /// <summary>
    /// The boundary that conversion exists for: the last second of the chosen day is covered, and
    /// the first instant of the next day is not.
    /// </summary>
    [Fact]
    public void A_grant_ending_on_a_day_covers_that_whole_day_and_not_the_next()
    {
        var period = new EntitlementPeriod(
            new DateTime(2026, 12, 1, 9, 30, 0, DateTimeKind.Utc),
            ManualPlanGrantPolicy.ExclusiveEndOf(new DateOnly(2026, 12, 31)));

        Assert.True(period.Covers(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)));
        Assert.False(period.Covers(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Reading the stored instant back as the date an administrator chose, so the page they edit on
    /// shows the day they picked rather than the exclusive instant underneath it. Midnight is the
    /// previous day's end; any other instant is a grant that stopped <em>during</em> its own day,
    /// which is what ending one immediately produces.
    /// </summary>
    [Fact]
    public void The_stored_instant_reads_back_as_the_last_day_it_covered()
    {
        Assert.Equal(
            new DateOnly(2026, 12, 31),
            ManualPlanGrantPolicy.InclusiveLastDayOf(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(
            new DateOnly(2026, 10, 29),
            ManualPlanGrantPolicy.InclusiveLastDayOf(new DateTime(2026, 10, 29, 13, 45, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// An end date that is already over entitles nothing at any instant, so it is refused on the way
    /// in rather than stored as a grant that was dead when it was written.
    /// </summary>
    [Fact]
    public void An_end_date_that_has_already_passed_is_refused()
    {
        var nowUtc = new DateTime(2026, 10, 29, 13, 45, 0, DateTimeKind.Utc);

        Assert.False(ManualPlanGrantPolicy.EndsAfter(new DateOnly(2026, 10, 28), nowUtc));
        Assert.False(ManualPlanGrantPolicy.EndsAfter(new DateOnly(2026, 10, 1), nowUtc));
    }

    /// <summary>
    /// And today is accepted: the exclusive end is the start of tomorrow, so the grant covers the
    /// rest of the day it was decided on.
    /// </summary>
    [Fact]
    public void An_end_date_of_today_still_covers_the_rest_of_today()
    {
        var nowUtc = new DateTime(2026, 10, 29, 13, 45, 0, DateTimeKind.Utc);

        Assert.True(ManualPlanGrantPolicy.EndsAfter(new DateOnly(2026, 10, 29), nowUtc));
        Assert.True(ManualPlanGrantPolicy.EndsAfter(new DateOnly(2026, 10, 30), nowUtc));
    }
}
