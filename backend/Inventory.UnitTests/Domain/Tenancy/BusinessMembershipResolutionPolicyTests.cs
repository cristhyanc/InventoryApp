using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic ownership rule behind issue #64's "missing, duplicate or ambiguous
/// membership must fail closed" requirement. Every denial case here is a security boundary:
/// resolving one of them to a business would hand a caller another business's financial data.
/// </summary>
public class BusinessMembershipResolutionPolicyTests
{
    private static ActorBusinessMembership Membership(
        int businessId,
        bool isActive = true,
        BusinessStatus businessStatus = BusinessStatus.Active,
        BusinessRole role = BusinessRole.Owner) =>
        new(
            BusinessId.From(businessId),
            role,
            isActive,
            businessStatus,
            StatusChangedAtUtc: new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc),
            BusinessRejectionReason: null);

    [Fact]
    public void Single_active_membership_in_an_active_business_resolves_that_business()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7)]);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(7), resolution.ResolvedBusinessId);
        Assert.Null(resolution.DenialReason);
    }

    /// <summary>
    /// The role the membership row carries is what resolution reports (issue #521) - the rule has
    /// no notion of a usual or default role, and does not derive one from anything else.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Operator)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public void The_resolved_business_carries_the_role_the_membership_records(BusinessRole role)
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7, role: role)]);

        Assert.True(resolution.IsResolved);
        Assert.Equal(role, resolution.ResolvedRole);
    }

    /// <summary>
    /// A stored role this code does not declare - <c>0</c> from a column an older schema never
    /// filled, a hand-written value, or a role a newer deployment wrote - denies access. The
    /// alternative, treating it as the lowest declared role, would grant an actor access nobody
    /// recorded, and would do so silently.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_unrecognised_stored_role_is_denied_and_resolves_no_business(int storedRole)
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(
            [Membership(7, role: (BusinessRole)storedRole)]);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.ResolvedBusinessId);
        Assert.Null(resolution.ResolvedRole);
        Assert.Equal(BusinessAccessDenialReason.RoleUnrecognised, resolution.DenialReason);
    }

    /// <summary>
    /// A membership denial is reported as that denial, not as a role problem: the role is only
    /// looked at for the membership that would otherwise have resolved, so an operator
    /// investigating an access failure is shown the cause they can act on.
    /// </summary>
    [Fact]
    public void A_membership_denial_outranks_an_unrecognised_role()
    {
        var unrecognised = (BusinessRole)0;

        Assert.Equal(
            BusinessAccessDenialReason.MembershipInactive,
            BusinessMembershipResolutionPolicy
                .Resolve([Membership(7, isActive: false, role: unrecognised)])
                .DenialReason);

        Assert.Equal(
            BusinessAccessDenialReason.MembershipAmbiguous,
            BusinessMembershipResolutionPolicy
                .Resolve([Membership(7, role: unrecognised), Membership(8, role: unrecognised)])
                .DenialReason);

        Assert.Equal(
            BusinessAccessDenialReason.BusinessInactive,
            BusinessMembershipResolutionPolicy
                .Resolve([Membership(7, businessStatus: BusinessStatus.Deactivated, role: unrecognised)])
                .DenialReason);
    }

    /// <summary>
    /// A revoked membership carrying an unrecognised role must not stop the one active membership
    /// from resolving: the role check applies to the membership that wins, not to the whole list.
    /// </summary>
    [Fact]
    public void A_revoked_membership_with_an_unrecognised_role_does_not_block_the_active_one()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(
            [Membership(7, isActive: false, role: (BusinessRole)0), Membership(8, role: BusinessRole.Manager)]);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(8), resolution.ResolvedBusinessId);
        Assert.Equal(BusinessRole.Manager, resolution.ResolvedRole);
    }

    [Fact]
    public void No_membership_is_denied_as_missing()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([]);

        AssertDenied(resolution, BusinessAccessDenialReason.MembershipMissing);
    }

    [Fact]
    public void Null_membership_collection_is_denied_rather_than_throwing()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(null);

        AssertDenied(resolution, BusinessAccessDenialReason.MembershipMissing);
    }

    [Fact]
    public void Only_revoked_memberships_are_denied_as_inactive()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7, isActive: false)]);

        AssertDenied(resolution, BusinessAccessDenialReason.MembershipInactive);
    }

    /// <summary>
    /// Two active memberships in different businesses. There is no tenant switching in this
    /// rollout, so the current business is undecidable and must not be guessed.
    /// </summary>
    [Fact]
    public void Memberships_in_two_businesses_are_denied_as_ambiguous()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7), Membership(8)]);

        AssertDenied(resolution, BusinessAccessDenialReason.MembershipAmbiguous);
    }

    /// <summary>
    /// Duplicate active rows for the same business. Collapsing them to one would be convenient
    /// and wrong: a duplicate means the membership data is not trustworthy, so access stops.
    /// </summary>
    [Fact]
    public void Duplicate_active_memberships_for_one_business_are_denied_as_ambiguous()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7), Membership(7)]);

        AssertDenied(resolution, BusinessAccessDenialReason.MembershipAmbiguous);
    }

    /// <summary>
    /// A revoked membership must not turn a still-valid single membership into an ambiguous one,
    /// otherwise offboarding one actor's old business would lock them out of their current one.
    /// </summary>
    [Fact]
    public void Revoked_membership_alongside_one_active_membership_still_resolves()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(
            [Membership(7, isActive: false), Membership(8)]);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(8), resolution.ResolvedBusinessId);
    }

    /// <summary>
    /// Only <see cref="BusinessStatus.Active"/> resolves (issue #540). A business that has applied
    /// and is waiting, one whose application was refused, and one that was offboarded are all
    /// denied with the same existing reason: the lifecycle status tells those situations apart for
    /// the account-state screen, and <em>never</em> for access.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public void Active_membership_in_a_business_that_is_not_active_is_denied(BusinessStatus status)
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7, businessStatus: status)]);

        AssertDenied(resolution, BusinessAccessDenialReason.BusinessInactive);
    }

    /// <summary>
    /// A stored status this code does not declare - <c>0</c> from a column an older schema never
    /// filled, a hand-written value, or a status a newer deployment wrote - denies access, for the
    /// same reason an undeclared role does: resolving it would grant access on a business state
    /// nobody here can reason about. The deny direction is the default rather than a case somebody
    /// has to remember to add.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_undeclared_stored_business_status_is_denied(int storedStatus)
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(
            [Membership(7, businessStatus: (BusinessStatus)storedStatus)]);

        AssertDenied(resolution, BusinessAccessDenialReason.BusinessInactive);
    }

    /// <summary>
    /// The membership rules are unchanged by the status: a revoked membership in an active
    /// business is still denied as inactive, and the denial an operator is shown stays the one
    /// they can act on.
    /// </summary>
    [Fact]
    public void A_membership_denial_still_outranks_a_business_that_is_not_active()
    {
        Assert.Equal(
            BusinessAccessDenialReason.MembershipInactive,
            BusinessMembershipResolutionPolicy
                .Resolve([Membership(7, isActive: false, businessStatus: BusinessStatus.Pending)])
                .DenialReason);
    }

    private static void AssertDenied(BusinessMembershipResolution resolution, BusinessAccessDenialReason expected)
    {
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.ResolvedBusinessId);
        Assert.Equal(expected, resolution.DenialReason);
    }
}
