using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace Inventory.UnitTests.Domain.Subscriptions;

/// <summary>
/// Who the grandfathering step of issue #966 grants Pro to, row by row.
///
/// <para>Each case is one the acceptance criteria name: a trading business, an offboarded one, a
/// pending application, a rejected one, a business created at and after the cutoff, and - the case
/// that makes a rerun safe - a pending application that was approved <em>after</em> the cutoff and is
/// Active by the time the command runs again.</para>
/// </summary>
public class GrandfatheringEligibilityPolicyTests
{
    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime BeforeCutoff = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterCutoff = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_grandfathered_plan_is_pro() =>
        Assert.Equal(SubscriptionPlan.Pro, GrandfatheringEligibilityPolicy.GrandfatheredPlan);

    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Deactivated)]
    public void A_business_trading_or_offboarded_before_the_cutoff_is_grandfathered(BusinessStatus status) =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.Eligible,
            Candidate(status, BeforeCutoff, decidedAtUtc: null));

    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    public void An_application_that_was_not_trading_at_the_cutoff_is_not_grandfathered(BusinessStatus status) =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.NotTradingAtCutoff,
            Candidate(status, BeforeCutoff, decidedAtUtc: null));

    /// <summary>
    /// A status this code does not declare is not trading either. The refusal direction is the
    /// default rather than a case somebody has to remember, which is the same rule
    /// <c>BusinessMembershipResolutionPolicy</c> applies to an undeclared status.
    /// </summary>
    [Fact]
    public void A_status_this_code_does_not_declare_is_not_grandfathered() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.NotTradingAtCutoff,
            Candidate((BusinessStatus)0, BeforeCutoff, decidedAtUtc: null));

    [Fact]
    public void A_business_created_after_the_cutoff_is_not_grandfathered() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.CreatedAtOrAfterCutoff,
            Candidate(BusinessStatus.Active, AfterCutoff, decidedAtUtc: null));

    /// <summary>
    /// The cutoff instant itself is <em>not</em> before the cutoff: created at it means created
    /// after the line was drawn. Asserted at the exact instant because this is the boundary the
    /// whole step is decided by.
    /// </summary>
    [Fact]
    public void A_business_created_at_the_exact_cutoff_is_not_grandfathered() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.CreatedAtOrAfterCutoff,
            Candidate(BusinessStatus.Active, Cutoff, decidedAtUtc: null));

    [Fact]
    public void A_business_created_one_tick_before_the_cutoff_is_grandfathered() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.Eligible,
            Candidate(BusinessStatus.Active, Cutoff.AddTicks(-1), decidedAtUtc: null));

    /// <summary>
    /// The criterion "a pending application approved later starts on Free", and the reason the
    /// decision instant is part of the rule: by the time a rerun reads this business it is Active
    /// and was created before the cutoff, so status and creation alone would grandfather it.
    /// </summary>
    [Fact]
    public void An_application_approved_after_the_cutoff_is_not_grandfathered_even_once_it_is_active() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.DecidedAtOrAfterCutoff,
            Candidate(BusinessStatus.Active, BeforeCutoff, AfterCutoff));

    [Fact]
    public void An_application_decided_at_the_exact_cutoff_is_not_grandfathered() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.DecidedAtOrAfterCutoff,
            Candidate(BusinessStatus.Active, BeforeCutoff, Cutoff));

    /// <summary>A business approved before the cutoff was trading at it, so it is grandfathered.</summary>
    [Fact]
    public void An_application_approved_before_the_cutoff_is_grandfathered() =>
        AssertDecision(
            GrandfatheringEligibilityPolicy.Decision.Eligible,
            Candidate(BusinessStatus.Active, BeforeCutoff, BeforeCutoff.AddDays(1)));

    /// <summary>
    /// The rules are applied in a fixed order, so a business refused by two of them reports the
    /// first - which is what keeps a report's reason stable rather than dependent on evaluation
    /// order.
    /// </summary>
    [Fact]
    public void A_business_refused_by_several_rules_reports_the_first()
    {
        var candidate = Candidate(BusinessStatus.Pending, AfterCutoff, AfterCutoff);

        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.CreatedAtOrAfterCutoff,
            GrandfatheringEligibilityPolicy.Decide(candidate, Cutoff));
    }

    [Fact]
    public void A_null_candidate_is_refused() =>
        Assert.Throws<ArgumentNullException>(() =>
            GrandfatheringEligibilityPolicy.Decide(null!, Cutoff));

    private static void AssertDecision(
        GrandfatheringEligibilityPolicy.Decision expected,
        GrandfatheringCandidate candidate)
    {
        Assert.Equal(expected, GrandfatheringEligibilityPolicy.Decide(candidate, Cutoff));
        Assert.Equal(
            expected == GrandfatheringEligibilityPolicy.Decision.Eligible,
            GrandfatheringEligibilityPolicy.IsEligible(candidate, Cutoff));
    }

    private static GrandfatheringCandidate Candidate(
        BusinessStatus status,
        DateTime createdAtUtc,
        DateTime? decidedAtUtc) =>
        new(BusinessId: 7, status, createdAtUtc, decidedAtUtc);
}
