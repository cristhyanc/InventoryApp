using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.Subscriptions;

/// <summary>
/// The beta Pro grant rule, row by row (issue #1258): what it grants, and the two conditions under
/// which an approval writes it.
/// </summary>
public class BetaProGrantPolicyTests
{
    private static readonly DateTime ApprovedAt = new(2026, 11, 18, 22, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// What the grant is, pinned literally: the public promise is Pro, the source says why, and the
    /// reason is the sentence the owner fixed on 14 November 2026.
    /// </summary>
    [Fact]
    public void The_grant_is_open_ended_pro_with_the_beta_source_and_the_fixed_reason()
    {
        Assert.Equal(SubscriptionPlan.Pro, BetaProGrantPolicy.GrantedPlan);
        Assert.Equal(ManualPlanGrantSource.Beta, BetaProGrantPolicy.GrantSource);
        Assert.Equal("Public beta introductory Pro access", BetaProGrantPolicy.GrantReason);

        var period = BetaProGrantPolicy.PeriodFrom(ApprovedAt);
        Assert.Equal(ApprovedAt, period.StartsAtUtc);
        Assert.Null(period.EndsAtUtc);
        Assert.True(period.IsWellFormed);

        // It entitles from the approval instant itself, inclusive, and never ends on its own.
        Assert.True(period.Covers(ApprovedAt));
        Assert.False(period.Covers(ApprovedAt.AddTicks(-1)));
        Assert.True(period.Covers(ApprovedAt.AddYears(10)));
    }

    /// <summary>
    /// The reason is storable through the surface that stores it: a fixed string nobody types is
    /// still subject to the one reason rule, so a change to either would fail here rather than at a
    /// database column.
    /// </summary>
    [Fact]
    public void The_fixed_reason_is_one_the_manual_grant_rules_accept()
    {
        Assert.Equal(
            BetaProGrantPolicy.GrantReason,
            ManualPlanGrantPolicy.NormaliseReason(BetaProGrantPolicy.GrantReason));
        Assert.False(ManualPlanGrantPolicy.IsReasonTooLong(BetaProGrantPolicy.GrantReason));
        Assert.True(ManualPlanGrantPolicy.IsGrantable(BetaProGrantPolicy.GrantedPlan));
    }

    [Fact]
    public void An_approval_grants_while_the_setting_is_on_and_the_business_holds_no_grant()
    {
        Assert.True(BetaProGrantPolicy.GrantsOnApproval(settingEnabled: true, []));
    }

    /// <summary>The shipped state, and the whole of what switching the setting off does to a new approval.</summary>
    [Fact]
    public void An_approval_grants_nothing_while_the_setting_is_off()
    {
        Assert.False(BetaProGrantPolicy.GrantsOnApproval(settingEnabled: false, []));
        Assert.False(BetaProGrantPolicy.GrantsOnApproval(settingEnabled: false, [Grant(ApprovedAt, null)]));
    }

    /// <summary>
    /// A business that already holds a manual grant is left alone, whether that grant currently
    /// applies, has not started yet or was ended before the approval. Each case is one a Super Admin
    /// decided, and writing a second grant or rewriting that row is not this rule's to do.
    /// </summary>
    [Theory]
    [InlineData(-10, null)]              // active and open-ended
    [InlineData(-10, 10)]                // active with an end date still ahead
    [InlineData(-10, -1)]                // ended before the approval
    [InlineData(5, null)]                // granted from a future instant
    public void An_approval_grants_nothing_to_a_business_that_already_holds_a_grant(
        int startsInDays,
        int? endsInDays)
    {
        var grant = Grant(
            ApprovedAt.AddDays(startsInDays),
            endsInDays is { } days ? ApprovedAt.AddDays(days) : null);

        Assert.False(BetaProGrantPolicy.GrantsOnApproval(settingEnabled: true, [grant]));
    }

    [Fact]
    public void The_grants_a_business_holds_are_required()
    {
        Assert.Throws<ArgumentNullException>(
            () => BetaProGrantPolicy.GrantsOnApproval(settingEnabled: true, null!));
    }

    private static ManualPlanGrantState Grant(DateTime startsAtUtc, DateTime? endsAtUtc) =>
        new(
            SubscriptionPlan.Starter,
            ManualPlanGrantSource.Complimentary,
            new EntitlementPeriod(startsAtUtc, endsAtUtc));
}
