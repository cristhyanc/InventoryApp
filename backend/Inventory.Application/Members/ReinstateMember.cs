using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// Gives a revoked membership back (issue #533), with the role the row still holds.
///
/// It is refused with <c>409</c> when that person now holds an active membership anywhere else. A
/// person belongs to one business at a time - the whole tenant boundary resolves a request to one
/// business from one active membership - so reinstating them here would deny them both rather than
/// offer a choice. <see cref="MembershipEligibility"/> is the rule, re-checked inside issue #522's
/// write transaction with this membership's own row excluded, and the refusal names no business,
/// member or role: the Owner being told this is not thereby entitled to know where that person
/// went.
///
/// Reinstating an already-active membership is accepted and writes nothing, for the same reason
/// revoking an already-revoked one is.
/// </summary>
public sealed class ReinstateMember
{
    private readonly MemberChangeWriter _writer;

    public ReinstateMember(MemberChangeWriter writer)
    {
        _writer = writer;
    }

    /// <returns>
    /// The member as the reinstatement left them, or <see langword="null"/> when the caller's
    /// business holds no such membership.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">The caller has no usable membership.</exception>
    /// <exception cref="DomainConflictException">
    /// That person already holds an active membership in a business, or the membership's stored
    /// sign-in identity is not usable.
    /// </exception>
    public Task<BusinessMember?> Handle(int membershipId, CancellationToken cancellationToken) =>
        _writer.ApplyAsync(
            membershipId,
            MemberChange.Reinstated,
            current => new MemberTargetState(current.StoredRole, IsActive: true),
            cancellationToken);
}
