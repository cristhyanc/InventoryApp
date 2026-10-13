using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The account-state precedence of issue #523, now reading the business lifecycle status issue
/// #540 added: the one rule that turns an identity's membership records into the state
/// <c>GET /api/me/account-state</c> reports.
///
/// Unlike <see cref="BusinessMembershipResolutionPolicy"/> this rule grants nothing - it only
/// describes. What it has to get right is the ordering: a person who holds a current membership
/// must be told they are a member whatever old revocations their history contains, because the
/// state decides which screen they are shown, and showing a working account the "your access was
/// revoked" screen is as wrong as the reverse.
/// </summary>
public class AccountStatePolicyTests
{
    private static readonly DateTime Earlier = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private static ActorBusinessMembership Membership(
        int businessId,
        bool isActive = true,
        BusinessStatus businessStatus = BusinessStatus.Active,
        BusinessRole role = BusinessRole.Owner,
        DateTime? statusChangedAtUtc = null,
        string? rejectionReason = null) =>
        new(
            BusinessId.From(businessId),
            role,
            isActive,
            businessStatus,
            statusChangedAtUtc ?? Earlier,
            rejectionReason);

    private static AccountState StateOf(params ActorBusinessMembership[] memberships) =>
        AccountStatePolicy.Determine(memberships).State;

    [Fact]
    public void No_membership_rows_at_all_is_NoMembership()
    {
        Assert.Equal(AccountState.NoMembership, StateOf());
    }

    [Fact]
    public void A_null_collection_is_NoMembership_rather_than_throwing()
    {
        Assert.Equal(AccountState.NoMembership, AccountStatePolicy.Determine(null).State);
    }

    [Fact]
    public void One_active_membership_in_an_active_business_is_Member()
    {
        Assert.Equal(AccountState.Member, StateOf(Membership(7)));
    }

    [Fact]
    public void One_active_membership_in_a_deactivated_business_is_BusinessDeactivated()
    {
        Assert.Equal(
            AccountState.BusinessDeactivated,
            StateOf(Membership(7, businessStatus: BusinessStatus.Deactivated)));
    }

    /// <summary>
    /// Step 3 of the precedence, which issue #540 made reachable: the person holds a real
    /// membership - which is what makes the application theirs - in a business that has applied
    /// and is waiting for a decision. They are shown the waiting screen rather than the
    /// deactivated one, and no endpoint serves the business's data either way.
    /// </summary>
    [Fact]
    public void One_active_membership_in_a_pending_business_is_ApplicationPending()
    {
        var decision = AccountStatePolicy.Determine([Membership(7, businessStatus: BusinessStatus.Pending)]);

        Assert.Equal(AccountState.ApplicationPending, decision.State);
        Assert.Null(decision.RejectionReason);
    }

    /// <summary>
    /// A status this code does not declare is not a screen anybody can be shown, and it must not
    /// read as a working account: the fail-closed answer is the same one a deactivated business
    /// gets. Access is denied for it either way, by
    /// <see cref="BusinessMembershipResolutionPolicy"/>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void One_active_membership_in_a_business_whose_stored_status_is_undeclared_is_BusinessDeactivated(
        int storedStatus)
    {
        Assert.Equal(
            AccountState.BusinessDeactivated,
            StateOf(Membership(7, businessStatus: (BusinessStatus)storedStatus)));
    }

    /// <summary>
    /// An <em>active</em> membership in a rejected business is not a state the approval workflow
    /// produces - rejecting an application revokes its memberships in the same operation - so the
    /// answer here is the fail-closed one rather than a rejection screen: the account reaches
    /// nothing, and the rejection reason belongs to the inactive case below.
    /// </summary>
    [Fact]
    public void One_active_membership_in_a_rejected_business_is_BusinessDeactivated_and_carries_no_reason()
    {
        var decision = AccountStatePolicy.Determine(
            [Membership(7, businessStatus: BusinessStatus.Rejected, rejectionReason: "Not a vending operation.")]);

        Assert.Equal(AccountState.BusinessDeactivated, decision.State);
        Assert.Null(decision.RejectionReason);
    }

    [Fact]
    public void Only_revoked_memberships_is_MembershipRevoked()
    {
        Assert.Equal(AccountState.MembershipRevoked, StateOf(Membership(7, isActive: false)));
    }

    /// <summary>
    /// A revoked membership in a business that was itself deactivated is still a revocation: the
    /// state describes the person's own membership, and the business's state only matters while
    /// the membership is current. A rejected business is the one exception the precedence names.
    /// </summary>
    [Fact]
    public void A_revoked_membership_in_a_deactivated_business_is_still_MembershipRevoked()
    {
        Assert.Equal(
            AccountState.MembershipRevoked,
            StateOf(Membership(7, isActive: false, businessStatus: BusinessStatus.Deactivated)));
    }

    /// <summary>
    /// Step 5's rejected case, which issue #540 made reachable: the applicant's membership was
    /// revoked when their business was refused, so they are told the application was rejected -
    /// with the reason the operator recorded - rather than that their membership was revoked.
    /// </summary>
    [Fact]
    public void An_inactive_membership_in_a_rejected_business_is_ApplicationRejected_with_its_reason()
    {
        var decision = AccountStatePolicy.Determine(
        [
            Membership(
                7,
                isActive: false,
                businessStatus: BusinessStatus.Rejected,
                rejectionReason: "The ABN provided belongs to another entity."),
        ]);

        Assert.Equal(AccountState.ApplicationRejected, decision.State);
        Assert.Equal("The ABN provided belongs to another entity.", decision.RejectionReason);
    }

    /// <summary>
    /// A rejected business with no recorded reason still reports the rejection: the reason is
    /// optional, and an absent one must not silently downgrade the state to a revocation, which
    /// describes something that did not happen.
    /// </summary>
    [Fact]
    public void An_inactive_membership_in_a_rejected_business_with_no_reason_is_still_ApplicationRejected()
    {
        var decision = AccountStatePolicy.Determine(
            [Membership(7, isActive: false, businessStatus: BusinessStatus.Rejected)]);

        Assert.Equal(AccountState.ApplicationRejected, decision.State);
        Assert.Null(decision.RejectionReason);
    }

    /// <summary>
    /// "The most recent inactive record decides" became observable with issue #540, because two
    /// inactive records can now give different states. A newer revocation outranks an older
    /// rejection: the person's most recent membership ended by being revoked, and showing them a
    /// rejection screen - with a reason from a business they later left - would describe the wrong
    /// event.
    /// </summary>
    [Fact]
    public void A_newer_revocation_outranks_an_older_rejection()
    {
        var decision = AccountStatePolicy.Determine(
        [
            Membership(
                7,
                isActive: false,
                businessStatus: BusinessStatus.Rejected,
                statusChangedAtUtc: Earlier,
                rejectionReason: "Incomplete application."),
            Membership(8, isActive: false, statusChangedAtUtc: Later),
        ]);

        Assert.Equal(AccountState.MembershipRevoked, decision.State);
        Assert.Null(decision.RejectionReason);
    }

    /// <summary>
    /// And the other direction: a rejection that happened after an older revocation is the record
    /// that decides, whichever order the records arrive in.
    /// </summary>
    [Fact]
    public void A_newer_rejection_outranks_an_older_revocation_whatever_order_the_records_arrive_in()
    {
        var rejected = Membership(
            8,
            isActive: false,
            businessStatus: BusinessStatus.Rejected,
            statusChangedAtUtc: Later,
            rejectionReason: "Duplicate application.");
        var revoked = Membership(7, isActive: false, statusChangedAtUtc: Earlier);

        foreach (var decision in new[]
        {
            AccountStatePolicy.Determine([rejected, revoked]),
            AccountStatePolicy.Determine([revoked, rejected]),
        })
        {
            Assert.Equal(AccountState.ApplicationRejected, decision.State);
            Assert.Equal("Duplicate application.", decision.RejectionReason);
        }
    }

    /// <summary>
    /// Two inactive records whose state changed at the very same instant must still give one
    /// deterministic answer rather than one that depends on the order persistence happened to
    /// return them in. The tie is broken towards the revocation, which is the state that claims
    /// less about what happened.
    /// </summary>
    [Fact]
    public void Inactive_records_that_changed_at_the_same_instant_resolve_deterministically()
    {
        ActorBusinessMembership[] records =
        [
            Membership(
                7,
                isActive: false,
                businessStatus: BusinessStatus.Rejected,
                statusChangedAtUtc: Later,
                rejectionReason: "Incomplete application."),
            Membership(8, isActive: false, statusChangedAtUtc: Later),
        ];

        Assert.Equal(AccountState.MembershipRevoked, AccountStatePolicy.Determine(records).State);
        Assert.Equal(
            AccountState.MembershipRevoked,
            AccountStatePolicy.Determine([records[1], records[0]]).State);
    }

    /// <summary>
    /// A rejection reason is reported only with <see cref="AccountState.ApplicationRejected"/>.
    /// A stored reason on a business somebody is still a current member of - however that came
    /// about - is not shown, because the state it belongs to is not the state they are in.
    /// </summary>
    [Fact]
    public void A_rejection_reason_is_never_reported_with_another_state()
    {
        var decision = AccountStatePolicy.Determine(
            [Membership(7, rejectionReason: "Rejected earlier and later approved.")]);

        Assert.Equal(AccountState.Member, decision.State);
        Assert.Null(decision.RejectionReason);
    }

    /// <summary>
    /// More than one active membership wins over everything else. It is only reachable for legacy
    /// data that predates issue #522's index, and it must not be resolved by choosing one: the
    /// person is shown an ambiguous account rather than one of two ledgers.
    /// </summary>
    [Fact]
    public void More_than_one_active_membership_is_MembershipAmbiguous()
    {
        Assert.Equal(AccountState.MembershipAmbiguous, StateOf(Membership(7), Membership(8)));
    }

    [Fact]
    public void Duplicate_active_memberships_for_one_business_are_also_MembershipAmbiguous()
    {
        Assert.Equal(AccountState.MembershipAmbiguous, StateOf(Membership(7), Membership(7)));
    }

    /// <summary>
    /// Ambiguity outranks every business status: the person holds two current memberships, and
    /// which of them would have decided the state is exactly what cannot be answered.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public void Ambiguity_outranks_the_state_either_active_membership_would_have_given(BusinessStatus status)
    {
        Assert.Equal(
            AccountState.MembershipAmbiguous,
            StateOf(Membership(7), Membership(8, businessStatus: status)));
    }

    /// <summary>
    /// The precedence requirement of issue #523: an old revocation never overrides a current
    /// membership. A person who was offboarded from one business years ago and is now a member of
    /// another is a <c>Member</c>, and the number and order of the revoked rows changes nothing -
    /// including a revoked row in a rejected business, whose rejection reason must not surface for
    /// somebody whose current membership works.
    /// </summary>
    [Fact]
    public void Old_revocations_never_override_a_current_membership()
    {
        var decision = AccountStatePolicy.Determine(
        [
            Membership(5, isActive: false, statusChangedAtUtc: Later),
            Membership(
                6,
                isActive: false,
                businessStatus: BusinessStatus.Rejected,
                statusChangedAtUtc: Later,
                rejectionReason: "Rejected."),
            Membership(7, statusChangedAtUtc: Earlier),
        ]);

        Assert.Equal(AccountState.Member, decision.State);
        Assert.Null(decision.RejectionReason);

        Assert.Equal(AccountState.Member, StateOf(Membership(7), Membership(5, isActive: false)));
    }

    /// <summary>
    /// The same ordering for a member of a pending or deactivated business: their current
    /// membership decides, so they are shown the screen for it rather than a revocation screen
    /// from an older record.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending, AccountState.ApplicationPending)]
    [InlineData(BusinessStatus.Deactivated, AccountState.BusinessDeactivated)]
    public void A_revoked_record_does_not_override_a_current_membership(
        BusinessStatus status,
        AccountState expected)
    {
        Assert.Equal(
            expected,
            StateOf(
                Membership(5, isActive: false, statusChangedAtUtc: Later),
                Membership(7, businessStatus: status, statusChangedAtUtc: Earlier)));
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
        Assert.Equal(AccountState.Member, StateOf(Membership(7, role: (BusinessRole)0)));
    }
}
