using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Members;

/// <summary>
/// Stops one member's access to the current business (issue #533).
///
/// The membership row is kept and marked inactive rather than deleted, so the revoked approval
/// stays visible on the members list, can be reinstated, and stops occupying the identity's one
/// active membership - which is what lets that person join another business. Access stops on the
/// target's next request: membership resolution is memoised per request only, so there is no cache
/// to wait for and no sign-out to force.
///
/// Revoking the business's last active Owner is refused, including when that Owner is the caller.
/// Only an Owner may manage members, roles, the integration and the business record, so a business
/// that loses its last active Owner cannot be administered by anybody inside it. The rule is
/// re-checked inside issue #522's write transaction, on the state the revocation would leave
/// behind.
///
/// Revoking an already-revoked membership is accepted and writes nothing, so a replayed request
/// neither re-stamps the instant the membership's state changed nor records a second audit event
/// for a change that did not happen.
/// </summary>
public sealed class RevokeMember
{
    private readonly MemberChangeWriter _writer;

    public RevokeMember(MemberChangeWriter writer)
    {
        _writer = writer;
    }

    /// <returns>
    /// The member as the revocation left them, or <see langword="null"/> when the caller's business
    /// holds no such membership.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">The caller has no usable membership.</exception>
    /// <exception cref="DomainConflictException">
    /// The revocation would leave the business with no active Owner.
    /// </exception>
    public Task<BusinessMember?> Handle(int membershipId, CancellationToken cancellationToken) =>
        _writer.ApplyAsync(
            membershipId,
            MemberChange.Revoked,
            // The role is carried through unchanged: a revoked row is history, and it is the role
            // a reinstatement restores.
            current => new MemberTargetState(current.StoredRole, IsActive: false),
            cancellationToken);
}
