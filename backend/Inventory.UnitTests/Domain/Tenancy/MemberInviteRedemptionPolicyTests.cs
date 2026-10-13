using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic rules of an invite redemption (issue #532): which invite may be redeemed at
/// all, and which sentence a caller who may not redeem is refused with.
///
/// Both halves are pure functions of the values handed in, which is the point: the redemption use
/// case decides nothing itself, so the one rule that says "this link is usable" cannot drift
/// between the endpoint, the conditional write and a later caller.
/// </summary>
public class MemberInviteRedemptionPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime ExpiresAt = Now.AddDays(1);

    #region Which invite is redeemable

    [Fact]
    public void A_live_invite_of_an_active_business_in_a_declared_role_is_redeemable() =>
        Assert.True(MemberInviteRedemptionPolicy.IsRedeemable(
            MemberInviteStatus.Pending,
            ExpiresAt,
            BusinessStatus.Active,
            BusinessRole.Manager,
            Now));

    /// <summary>
    /// Used, cancelled, expired and undeclared states are all unusable, and the policy says so
    /// through <see cref="MemberInvitePolicy.IsLive"/> rather than by listing them again - one
    /// liveness rule serves the list, the cancellation and the redemption.
    /// </summary>
    [Theory]
    [InlineData(MemberInviteStatus.Used)]
    [InlineData(MemberInviteStatus.Cancelled)]
    [InlineData(MemberInviteStatus.Expired)]
    [InlineData((MemberInviteStatus)99)]
    public void An_invite_that_is_not_pending_is_not_redeemable(MemberInviteStatus storedStatus) =>
        Assert.False(MemberInviteRedemptionPolicy.IsRedeemable(
            storedStatus,
            ExpiresAt,
            BusinessStatus.Active,
            BusinessRole.Manager,
            Now));

    /// <summary>
    /// The expiry boundary is inclusive, exactly as the rest of the invite rules read it: seven
    /// days of validity is the promise, and a link that still worked at its expiry instant
    /// outlived it.
    /// </summary>
    [Fact]
    public void A_pending_invite_stops_being_redeemable_at_its_expiry_instant()
    {
        Assert.True(MemberInviteRedemptionPolicy.IsRedeemable(
            MemberInviteStatus.Pending,
            Now.AddTicks(1),
            BusinessStatus.Active,
            BusinessRole.Manager,
            Now));
        Assert.False(MemberInviteRedemptionPolicy.IsRedeemable(
            MemberInviteStatus.Pending,
            Now,
            BusinessStatus.Active,
            BusinessRole.Manager,
            Now));
    }

    /// <summary>
    /// Only an <c>Active</c> business's invite may be redeemed, and the rule is written as "is
    /// Active" rather than as a list of the statuses that are not, so a status a newer deployment
    /// wrote refuses the redemption instead of letting somebody into a business nobody can reach.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData((BusinessStatus)99)]
    public void An_invite_of_a_business_that_is_not_active_is_not_redeemable(BusinessStatus businessStatus) =>
        Assert.False(MemberInviteRedemptionPolicy.IsRedeemable(
            MemberInviteStatus.Pending,
            ExpiresAt,
            businessStatus,
            BusinessRole.Manager,
            Now));

    /// <summary>
    /// An invite whose stored role this deployment does not declare is refused rather than
    /// redeemed. Honouring it would create a membership that is denied access everywhere
    /// (<c>RoleUnrecognised</c>) - a person let in and then locked out, with no way back except an
    /// Owner noticing. Only a hand-written <c>UPDATE</c>, or a role a newer deployment wrote, can
    /// produce one.
    /// </summary>
    [Theory]
    [InlineData((BusinessRole)0)]
    [InlineData((BusinessRole)99)]
    public void An_invite_granting_an_undeclared_role_is_not_redeemable(BusinessRole role) =>
        Assert.False(MemberInviteRedemptionPolicy.IsRedeemable(
            MemberInviteStatus.Pending,
            ExpiresAt,
            BusinessStatus.Active,
            role,
            Now));

    [Theory]
    [InlineData(BusinessRole.Operator)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public void Every_declared_role_may_be_granted_by_an_invite(BusinessRole role) =>
        Assert.True(MemberInviteRedemptionPolicy.IsRedeemable(
            MemberInviteStatus.Pending,
            ExpiresAt,
            BusinessStatus.Active,
            role,
            Now));

    #endregion

    #region Which sentence a refusal carries

    /// <summary>
    /// The one refusal that is its own sentence: a caller whose business was deactivated is told
    /// they must first be released by the platform administrator, because revoking their own
    /// membership is not something they can do and "you are already a member of a business" would
    /// send them looking for an Owner who cannot be reached.
    /// </summary>
    [Fact]
    public void A_deactivated_business_caller_is_told_to_be_released_by_the_platform_administrator()
    {
        Assert.Equal(
            MemberInviteRedemptionPolicy.ReleaseRequiredMessage,
            MemberInviteRedemptionPolicy.IneligibleMessage(AccountState.BusinessDeactivated));

        // And it names no business - not a name, not an id - because the caller being refused is
        // being refused for a business they may not be told about either.
        Assert.DoesNotContain("business ", MemberInviteRedemptionPolicy.ReleaseRequiredMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("platform administrator", MemberInviteRedemptionPolicy.ReleaseRequiredMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other refusal is the standing membership sentence, including the states that may
    /// redeem: the account state only ever chooses the wording, and
    /// <c>MembershipEligibility</c> alone decides whether a refusal happens at all.
    /// </summary>
    [Theory]
    [InlineData(AccountState.Member)]
    [InlineData(AccountState.ApplicationPending)]
    [InlineData(AccountState.MembershipAmbiguous)]
    [InlineData(AccountState.NoMembership)]
    [InlineData(AccountState.MembershipRevoked)]
    [InlineData(AccountState.ApplicationRejected)]
    [InlineData((AccountState)99)]
    public void Every_other_state_is_refused_with_the_standing_membership_sentence(AccountState state) =>
        Assert.Equal(
            MembershipEligibility.AlreadyAMemberMessage,
            MemberInviteRedemptionPolicy.IneligibleMessage(state));

    /// <summary>
    /// The generic invalid-invite sentence names no business and nothing about which of the four
    /// unusable cases it was, so an unknown token and a used one are indistinguishable to whoever
    /// submitted them.
    /// </summary>
    [Fact]
    public void The_invalid_invite_sentence_reveals_nothing_about_the_invite_or_its_business()
    {
        var message = MemberInviteRedemptionPolicy.InvalidInviteMessage;

        Assert.False(string.IsNullOrWhiteSpace(message));
        foreach (var revealing in new[] { "expired", "used", "cancelled", "withdrawn", "unknown", "deactivated" })
        {
            Assert.DoesNotContain($"has been {revealing}", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Eligibility stays one rule

    /// <summary>
    /// The redemption reads the caller's membership records to choose a refusal sentence, and the
    /// eligibility question it asks of them is the same one <c>MembershipWriteGuard</c> re-asks
    /// inside the write transaction. These two overloads of one rule must therefore agree on the
    /// same records, which is what stops the endpoint's fast refusal from having a rule of its own.
    /// </summary>
    [Theory]
    [InlineData(new bool[0], true)]
    [InlineData(new[] { false }, true)]
    [InlineData(new[] { false, false }, true)]
    [InlineData(new[] { true }, false)]
    [InlineData(new[] { false, true }, false)]
    [InlineData(new[] { true, true }, false)]
    public void Both_views_of_the_eligibility_rule_agree(bool[] activeFlags, bool expected)
    {
        var accountStateRecords = activeFlags
            .Select((isActive, index) => new ActorBusinessMembership(
                BusinessId.From(index + 1),
                BusinessRole.Manager,
                isActive,
                BusinessStatus.Active,
                Now,
                null))
            .ToList();

        var writePathRecords = activeFlags
            .Select((isActive, index) => new IdentityMembership(index + 1, isActive))
            .ToList();

        Assert.Equal(expected, MembershipEligibility.AllowsActiveMembership(accountStateRecords));
        Assert.Equal(expected, MembershipEligibility.AllowsActiveMembership(writePathRecords));
    }

    [Fact]
    public void No_membership_record_at_all_is_eligible() =>
        Assert.True(MembershipEligibility.AllowsActiveMembership((IReadOnlyCollection<ActorBusinessMembership>?)null));

    #endregion
}
