using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Frees one person from a deactivated business (issue #545, one slice of #509 under #328): their
/// membership becomes inactive, with the instant it changed recorded on the row, so they may redeem
/// an invite or apply for a business of their own.
///
/// <para><strong>Why this exists at all.</strong> A person belongs to one business at a time
/// (<see cref="MembershipEligibility"/>), and a membership in a deactivated business still occupies
/// that one slot - the rule deliberately never reads the owning business's state. Releasing a
/// membership is normally the Owner's doing, but a deactivated business does not resolve, so its
/// Owner can act on nothing: without this endpoint a member of a business that was switched off could
/// never join anywhere else, which is the exact refusal
/// <see cref="MemberInviteRedemptionPolicy.ReleaseRequiredMessage"/> already tells them to come here
/// for.</para>
///
/// <para><strong>The last-Owner rule does not apply here, deliberately.</strong>
/// <see cref="BusinessOwnerRetention"/> protects a <em>trading</em> business from losing the only
/// person who can administer it. A business nobody can reach has no such access to protect, and
/// applying the rule here would leave its last Owner permanently attached to it - the very situation
/// this operation exists to end. What keeps that from being a hole is the pair of rules around it:
/// members may be released only while the business is <see cref="BusinessStatus.Deactivated"/>, and
/// <see cref="ReactivateBusiness"/> refuses to switch a business back on while it has no active
/// Owner.</para>
///
/// <para>Releasing a membership that is already inactive changes nothing and is audited as nothing,
/// so a replayed request neither re-stamps the instant the membership's state changed nor records a
/// second event for a change that did not happen.</para>
///
/// <para>The transaction, the status rule and the audit event are
/// <see cref="BusinessLifecycleWriter"/>'s, shared with the two actions beside it.</para>
/// </summary>
public sealed class ReleaseBusinessMember
{
    private readonly BusinessLifecycleWriter _writer;

    public ReleaseBusinessMember(BusinessLifecycleWriter writer)
    {
        _writer = writer;
    }

    /// <param name="businessId">The deactivated business the membership must belong to.</param>
    /// <param name="membershipId">
    /// The membership to release, looked up only within that business - so another business's key,
    /// and a key that exists nowhere, are the same answer.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    /// <returns>
    /// The member as the release left them, or <see langword="null"/> when that business holds no
    /// such membership.
    /// </returns>
    /// <exception cref="DomainConflictException">
    /// The business is not deactivated, so its members are its own Owner's business. Nothing is
    /// committed.
    /// </exception>
    public Task<PlatformBusinessMember?> Handle(
        int businessId,
        int membershipId,
        CancellationToken cancellationToken) =>
        _writer.ReleaseMemberAsync(businessId, membershipId, cancellationToken);
}

/// <summary>
/// Every membership of one deactivated business, so the Super Admin can see who is still attached to
/// it and choose whom to release (issue #545).
///
/// <para>It is the one read in this application that reports another business's members, and it is
/// bounded in every direction: only while that business is deactivated, only the four fields of
/// <see cref="PlatformBusinessMember"/>, and never an email address or an Entra identifier. A
/// trading business's membership list stays its own Owner's (issue #533).</para>
/// </summary>
public sealed class ListDeactivatedBusinessMembers
{
    private readonly BusinessLifecycleWriter _writer;

    public ListDeactivatedBusinessMembers(BusinessLifecycleWriter writer)
    {
        _writer = writer;
    }

    /// <returns>
    /// The business's memberships, active and inactive alike, or <see langword="null"/> when no such
    /// business exists.
    /// </returns>
    /// <exception cref="DomainConflictException">The business is not deactivated.</exception>
    public Task<IReadOnlyList<PlatformBusinessMember>?> Handle(
        int businessId,
        CancellationToken cancellationToken) =>
        _writer.ListMembersAsync(businessId, cancellationToken);
}
