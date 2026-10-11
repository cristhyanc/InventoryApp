using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The one-active-membership eligibility rule (issue #522).
///
/// These are access-boundary tests, not list-filtering tests: every case below decides whether a
/// person is allowed to gain access to a business's financial data, and the two directions fail
/// differently. Allowing a second active membership strands the person - resolution then denies
/// them every business - while refusing one they are entitled to is a visible, correctable
/// refusal. The rule is therefore written and tested as "only while nothing else is active".
/// </summary>
public class MembershipEligibilityTests
{
    private static IdentityMembership Active(int id) => new(id, IsActive: true);

    private static IdentityMembership Revoked(int id) => new(id, IsActive: false);

    /// <summary>
    /// An identity with no membership at all. A <c>null</c> collection is the same answer as an
    /// empty one: a port that found nothing and a port that returned nothing both describe a
    /// person who is a member of no business.
    /// </summary>
    [Fact]
    public void An_identity_with_no_membership_may_gain_one()
    {
        Assert.True(MembershipEligibility.AllowsActiveMembership(null));
        Assert.True(MembershipEligibility.AllowsActiveMembership([]));
    }

    /// <summary>
    /// Revoked memberships never block. A person who left one business may join another, however
    /// many revoked rows their history holds - which is also why revoking is how a transfer is
    /// done.
    /// </summary>
    [Fact]
    public void Only_revoked_memberships_do_not_block_a_new_one()
    {
        Assert.True(MembershipEligibility.AllowsActiveMembership([Revoked(1)]));
        Assert.True(MembershipEligibility.AllowsActiveMembership([Revoked(1), Revoked(2), Revoked(3)]));
    }

    /// <summary>
    /// The rule itself. One active membership anywhere is enough to refuse another, and the
    /// collection's order does not change the answer.
    /// </summary>
    [Fact]
    public void An_active_membership_blocks_gaining_another()
    {
        Assert.False(MembershipEligibility.AllowsActiveMembership([Active(1)]));
        Assert.False(MembershipEligibility.AllowsActiveMembership([Revoked(1), Active(2)]));
        Assert.False(MembershipEligibility.AllowsActiveMembership([Active(2), Revoked(1)]));
    }

    /// <summary>
    /// The exclusion: an operation on an already-active membership must not be refused because
    /// that same row is active. Approving an applicant's own membership (#509) and changing a
    /// member's role (#504) both do exactly that, and counting the row against itself would make
    /// every such operation impossible.
    /// </summary>
    [Fact]
    public void The_excluded_membership_does_not_count_against_itself()
    {
        Assert.True(MembershipEligibility.AllowsActiveMembership([Active(7)], excludedMembershipId: 7));
        Assert.True(
            MembershipEligibility.AllowsActiveMembership([Active(7), Revoked(8)], excludedMembershipId: 7));
    }

    /// <summary>
    /// And the exclusion excludes one membership, not the rule. A second active membership
    /// somewhere else still refuses the operation - which is the case that would otherwise turn
    /// "exclude the row I am working on" into "skip the check".
    /// </summary>
    [Fact]
    public void A_second_active_membership_still_blocks_when_one_is_excluded()
    {
        Assert.False(
            MembershipEligibility.AllowsActiveMembership([Active(7), Active(9)], excludedMembershipId: 7));
        Assert.False(
            MembershipEligibility.AllowsActiveMembership([Active(7), Active(9)], excludedMembershipId: 9));
    }

    /// <summary>
    /// Excluding a membership that is not in the collection changes nothing: the exclusion is a
    /// filter over what was read, never an assertion that the row exists, so a stale id cannot
    /// make a blocked identity look eligible.
    /// </summary>
    [Fact]
    public void Excluding_an_unrelated_membership_id_changes_nothing()
    {
        Assert.False(MembershipEligibility.AllowsActiveMembership([Active(1)], excludedMembershipId: 99));
        Assert.True(MembershipEligibility.AllowsActiveMembership([Revoked(1)], excludedMembershipId: 99));
    }

    /// <summary>
    /// The refusal message is one fixed sentence about the caller's own membership. It must name
    /// no business, no role and no other member: a person being told they may not join is not
    /// thereby entitled to know which business they already belong to.
    /// </summary>
    [Fact]
    public void The_refusal_message_describes_the_person_and_no_business()
    {
        Assert.Equal("This person is already a member of a business.", MembershipEligibility.AlreadyAMemberMessage);
    }
}
