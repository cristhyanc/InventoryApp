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
    private static ActorBusinessMembership Membership(int businessId, bool isActive = true, bool businessIsActive = true) =>
        new(BusinessId.From(businessId), isActive, businessIsActive);

    [Fact]
    public void Single_active_membership_in_an_active_business_resolves_that_business()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve([Membership(7)]);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(7), resolution.ResolvedBusinessId);
        Assert.Null(resolution.DenialReason);
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

    [Fact]
    public void Active_membership_in_a_deactivated_business_is_denied()
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(
            [Membership(7, businessIsActive: false)]);

        AssertDenied(resolution, BusinessAccessDenialReason.BusinessInactive);
    }

    private static void AssertDenied(BusinessMembershipResolution resolution, BusinessAccessDenialReason expected)
    {
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.ResolvedBusinessId);
        Assert.Equal(expected, resolution.DenialReason);
    }
}
