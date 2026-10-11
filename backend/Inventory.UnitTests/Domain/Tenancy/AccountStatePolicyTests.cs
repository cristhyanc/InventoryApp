using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The account-state precedence of issue #523: the one rule that turns an identity's membership
/// records into the state <c>GET /api/me/account-state</c> reports.
///
/// Unlike <see cref="BusinessMembershipResolutionPolicy"/> this rule grants nothing - it only
/// describes. What it has to get right is the ordering: a person who holds a current membership
/// must be told they are a member whatever old revocations their history contains, because the
/// state decides which screen they are shown, and showing a working account the "your access was
/// revoked" screen is as wrong as the reverse.
/// </summary>
public class AccountStatePolicyTests
{
    private static ActorBusinessMembership Membership(
        int businessId,
        bool isActive = true,
        bool businessIsActive = true,
        BusinessRole role = BusinessRole.Owner) =>
        new(BusinessId.From(businessId), role, isActive, businessIsActive);

    [Fact]
    public void No_membership_rows_at_all_is_NoMembership()
    {
        Assert.Equal(AccountState.NoMembership, AccountStatePolicy.Determine([]));
    }

    [Fact]
    public void A_null_collection_is_NoMembership_rather_than_throwing()
    {
        Assert.Equal(AccountState.NoMembership, AccountStatePolicy.Determine(null));
    }

    [Fact]
    public void One_active_membership_in_an_active_business_is_Member()
    {
        Assert.Equal(AccountState.Member, AccountStatePolicy.Determine([Membership(7)]));
    }

    [Fact]
    public void One_active_membership_in_a_deactivated_business_is_BusinessDeactivated()
    {
        Assert.Equal(
            AccountState.BusinessDeactivated,
            AccountStatePolicy.Determine([Membership(7, businessIsActive: false)]));
    }

    [Fact]
    public void Only_revoked_memberships_is_MembershipRevoked()
    {
        Assert.Equal(
            AccountState.MembershipRevoked,
            AccountStatePolicy.Determine([Membership(7, isActive: false)]));
    }

    /// <summary>
    /// A revoked membership in a business that was itself deactivated is still a revocation: the
    /// state describes the person's own membership, and the business's state only matters while
    /// the membership is current. <c>ApplicationRejected</c> is the one exception the precedence
    /// names, and it needs the <c>Rejected</c> business status issue #507 adds.
    /// </summary>
    [Fact]
    public void A_revoked_membership_in_a_deactivated_business_is_still_MembershipRevoked()
    {
        Assert.Equal(
            AccountState.MembershipRevoked,
            AccountStatePolicy.Determine([Membership(7, isActive: false, businessIsActive: false)]));
    }

    /// <summary>
    /// More than one active membership wins over everything else. It is only reachable for legacy
    /// data that predates issue #522's index, and it must not be resolved by choosing one: the
    /// person is shown an ambiguous account rather than one of two ledgers.
    /// </summary>
    [Fact]
    public void More_than_one_active_membership_is_MembershipAmbiguous()
    {
        Assert.Equal(
            AccountState.MembershipAmbiguous,
            AccountStatePolicy.Determine([Membership(7), Membership(8)]));
    }

    [Fact]
    public void Duplicate_active_memberships_for_one_business_are_also_MembershipAmbiguous()
    {
        Assert.Equal(
            AccountState.MembershipAmbiguous,
            AccountStatePolicy.Determine([Membership(7), Membership(7)]));
    }

    /// <summary>
    /// Ambiguity outranks a deactivated business: the person holds two current memberships, and
    /// which of them would have decided the state is exactly what cannot be answered.
    /// </summary>
    [Fact]
    public void Ambiguity_outranks_the_state_either_active_membership_would_have_given()
    {
        Assert.Equal(
            AccountState.MembershipAmbiguous,
            AccountStatePolicy.Determine([Membership(7), Membership(8, businessIsActive: false)]));
    }

    /// <summary>
    /// The precedence requirement of issue #523: an old revocation never overrides a current
    /// membership. A person who was offboarded from one business years ago and is now a member of
    /// another is a <c>Member</c>, and the number and order of the revoked rows changes nothing.
    /// </summary>
    [Fact]
    public void Old_revocations_never_override_a_current_membership()
    {
        Assert.Equal(
            AccountState.Member,
            AccountStatePolicy.Determine(
            [
                Membership(5, isActive: false),
                Membership(6, isActive: false, businessIsActive: false),
                Membership(7),
            ]));

        Assert.Equal(
            AccountState.Member,
            AccountStatePolicy.Determine(
            [
                Membership(7),
                Membership(5, isActive: false),
            ]));
    }

    /// <summary>
    /// The same ordering for a member of a deactivated business: their current membership decides,
    /// so they are shown the business-deactivated screen rather than a revocation screen from an
    /// older record.
    /// </summary>
    [Fact]
    public void A_revoked_record_does_not_override_a_current_membership_in_a_deactivated_business()
    {
        Assert.Equal(
            AccountState.BusinessDeactivated,
            AccountStatePolicy.Determine(
            [
                Membership(5, isActive: false),
                Membership(7, businessIsActive: false),
            ]));
    }

    /// <summary>
    /// The state is about the membership, not about what the member may do. A stored role this
    /// deployment does not declare is refused by <see cref="BusinessMembershipResolutionPolicy"/>
    /// at every business endpoint, and that refusal is not an account state: the membership is
    /// current, so the account state is <c>Member</c> and the role problem is an operator's to
    /// fix. Reporting <c>NoMembership</c> or a revocation here would send the person to a screen
    /// that describes a membership they still hold.
    /// </summary>
    [Fact]
    public void A_current_membership_whose_stored_role_is_undeclared_is_still_Member()
    {
        Assert.Equal(
            AccountState.Member,
            AccountStatePolicy.Determine([Membership(7, role: (BusinessRole)0)]));
    }

    /// <summary>
    /// The two application states of the precedence need the business status issue #507 adds, and
    /// no combination of the facts a membership carries today can produce one. This is the test
    /// that has to change when #507 arrives, rather than a frontend discovering the state it was
    /// promised never appears.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void The_application_states_issue_507_adds_are_never_reported_today(
        bool isActive,
        bool businessIsActive)
    {
        var state = AccountStatePolicy.Determine(
            [Membership(7, isActive: isActive, businessIsActive: businessIsActive)]);

        Assert.NotEqual(AccountState.ApplicationPending, state);
        Assert.NotEqual(AccountState.ApplicationRejected, state);
    }
}
