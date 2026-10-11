using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// Changes what one member of the current business may do (issue #533).
///
/// <para><strong>The submitted role is checked against the declared vocabulary first.</strong> An
/// enum is a compile-time constraint, not a validation: a JSON string and a form field both carry
/// arbitrary text, and <c>"40"</c> is a stored representation nothing outside the database gets to
/// supply. <see cref="BusinessRoles.TryParse"/> is the one check, so an unsupported value is
/// refused with <c>400</c> and nothing is stored.</para>
///
/// <para><strong>A role change can remove the business's last Owner</strong> - including when the
/// Owner demotes themselves, which is deliberately allowed while another active Owner remains. The
/// rule is re-checked on the state the write leaves behind by issue #522's shared write path, so
/// two Owners demoting each other at the same instant cannot both succeed.</para>
///
/// <para><strong>A revoked membership's role is not edited.</strong> Reinstating restores the role
/// the row holds, so changing it while the approval is revoked would queue up access nobody
/// reviewed for a later reinstatement. The Owner reinstates first and then changes the role, and
/// both steps are audited.</para>
/// </summary>
public sealed class ChangeMemberRole
{
    /// <summary>
    /// The refusal for a role change on a revoked membership. It states the rule and names no
    /// member, and it is published rather than internal because it is the exact sentence the
    /// <c>409</c> response body carries, which makes it part of the contract the tests pin.
    /// </summary>
    public const string RevokedMembershipMessage =
        "A revoked membership's role cannot be changed. Reinstate the member first.";

    private readonly MemberChangeWriter _writer;

    public ChangeMemberRole(MemberChangeWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Gives <paramref name="membershipId"/> the role named by <paramref name="role"/>.
    /// </summary>
    /// <returns>
    /// The member as the change left them, or <see langword="null"/> when the caller's business
    /// holds no such membership.
    /// </returns>
    /// <exception cref="DomainValidationException"><paramref name="role"/> is not a declared role name.</exception>
    /// <exception cref="DomainConflictException">
    /// The membership is revoked, or the change would leave the business with no active Owner.
    /// </exception>
    public Task<BusinessMember?> Handle(int membershipId, string? role, CancellationToken cancellationToken)
    {
        if (!BusinessRoles.TryParse(role, out var requested))
        {
            throw new DomainValidationException(BusinessRoles.UnsupportedMessage);
        }

        return _writer.ApplyAsync(
            membershipId,
            MemberChange.RoleChanged,
            current => current.Member.IsActive
                ? new MemberTargetState(requested, IsActive: true)
                : throw new DomainConflictException(RevokedMembershipMessage),
            cancellationToken);
    }
}
