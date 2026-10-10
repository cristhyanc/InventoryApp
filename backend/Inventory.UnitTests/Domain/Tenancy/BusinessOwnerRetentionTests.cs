using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The last-Owner rule (issue #522): a business always keeps at least one active Owner.
///
/// The failure this prevents is unrecoverable from inside the application. Only an Owner may
/// manage members and roles, so a business whose last active Owner is revoked or demoted cannot
/// be administered by anybody in it, and there is no cross-business write path that could repair
/// it - only a human editing the database.
/// </summary>
public class BusinessOwnerRetentionTests
{
    private static BusinessMembershipRole Active(BusinessRole role) => new(role, IsActive: true);

    private static BusinessMembershipRole Revoked(BusinessRole role) => new(role, IsActive: false);

    [Fact]
    public void One_active_owner_is_enough()
    {
        Assert.True(BusinessOwnerRetention.RetainsActiveOwner([Active(BusinessRole.Owner)]));
        Assert.True(BusinessOwnerRetention.RetainsActiveOwner(
            [Active(BusinessRole.Operator), Active(BusinessRole.Owner), Active(BusinessRole.Manager)]));
        Assert.True(BusinessOwnerRetention.RetainsActiveOwner(
            [Active(BusinessRole.Owner), Active(BusinessRole.Owner)]));
    }

    /// <summary>
    /// A revoked Owner is not an Owner. The row still says <c>Owner</c>, but that person cannot
    /// sign in, so the business has nobody who can administer it.
    /// </summary>
    [Fact]
    public void A_revoked_owner_does_not_count()
    {
        Assert.False(BusinessOwnerRetention.RetainsActiveOwner([Revoked(BusinessRole.Owner)]));
        Assert.False(BusinessOwnerRetention.RetainsActiveOwner(
            [Revoked(BusinessRole.Owner), Active(BusinessRole.Manager)]));
    }

    /// <summary>
    /// Nor does a Manager, however much else they may do: the capability table gives member and
    /// role management to the Owner role alone (issue #521).
    /// </summary>
    [Fact]
    public void No_other_role_stands_in_for_an_owner()
    {
        Assert.False(BusinessOwnerRetention.RetainsActiveOwner(
            [Active(BusinessRole.Manager), Active(BusinessRole.Operator)]));
    }

    /// <summary>
    /// A business with no membership at all keeps no Owner. The rule answers what the business
    /// still has, so the empty case is a refusal rather than a vacuous pass - and <c>null</c>, the
    /// answer of a port that read nothing, is the same.
    /// </summary>
    [Fact]
    public void A_business_with_no_membership_keeps_no_owner()
    {
        Assert.False(BusinessOwnerRetention.RetainsActiveOwner(null));
        Assert.False(BusinessOwnerRetention.RetainsActiveOwner([]));
    }

    /// <summary>
    /// A stored role this code does not declare is not an Owner. A row carrying <c>0</c> from an
    /// older schema, or a role a newer deployment wrote, must not be able to stand in for the
    /// Owner being removed: <see cref="BusinessMembershipResolutionPolicy"/> denies that member
    /// access entirely, so treating it as an Owner would leave the business with nobody.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(99)]
    public void An_undeclared_stored_role_is_not_an_owner(int storedRole)
    {
        Assert.False(BusinessOwnerRetention.RetainsActiveOwner([Active((BusinessRole)storedRole)]));
    }

    [Fact]
    public void The_refusal_message_states_the_rule_and_names_nobody()
    {
        Assert.Equal(
            "A business must keep at least one active Owner. Give another member the Owner role first.",
            BusinessOwnerRetention.LastActiveOwnerMessage);
    }
}
