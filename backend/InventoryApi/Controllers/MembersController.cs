using Inventory.Application.Access;
using Inventory.Application.Members;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The members of the signed-in Owner's business, and the three changes an Owner may make to one
/// (issue #533): list, change role, revoke and reinstate.
///
/// <para>Owner-only. The controller declares <c>Members.Manage</c>, which #328's agreed table
/// gives to an Owner alone, so every action here is refused for a Manager and an Operator by the
/// authorization middleware before it runs - with a bare <c>403</c> that describes neither the
/// capability nor the caller's role. Reading the list is as restricted as changing it: the list is
/// the roster the changes are made from, and it carries the display name and email address of
/// every member of the business.</para>
///
/// <para>No action takes a business identifier of any kind. The business is resolved from the
/// authenticated actor's membership, and the membership key each change takes is looked up only
/// within that business - so another business's key is simply not found, rather than refused in a
/// way that confirms it exists.</para>
///
/// <para>Refusals are mapped centrally. A membership rule - the person already belongs to a
/// business, the business must keep an active Owner, a revoked membership's role is not edited -
/// throws <c>DomainConflictException</c>, which <see cref="Http.DomainExceptionHandler"/> answers
/// as <c>409 Conflict</c> with the Domain's own sentence; an unsupported role name throws
/// <c>DomainValidationException</c> and is answered as <c>400</c>. Neither message names another
/// business, member or role.</para>
/// </summary>
[ApiController]
[Route("api/members")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.MembersManage)]
public sealed class MembersController : ControllerBase
{
    private readonly ListMembers _listMembers;
    private readonly ChangeMemberRole _changeRole;
    private readonly RevokeMember _revokeMember;
    private readonly ReinstateMember _reinstateMember;

    public MembersController(
        ListMembers listMembers,
        ChangeMemberRole changeRole,
        RevokeMember revokeMember,
        ReinstateMember reinstateMember)
    {
        _listMembers = listMembers;
        _changeRole = changeRole;
        _revokeMember = revokeMember;
        _reinstateMember = reinstateMember;
    }

    /// <summary>Every member of the caller's business, revoked ones included.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BusinessMemberResponse>>> List(CancellationToken cancellationToken)
    {
        var members = await _listMembers.Handle(cancellationToken);

        return Ok(members.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Gives one member a different role. The change applies on that member's next request.
    /// </summary>
    [HttpPut("{membershipId:int}/role")]
    public async Task<ActionResult<BusinessMemberResponse>> ChangeRole(
        int membershipId,
        ChangeMemberRoleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var member = await _changeRole.Handle(membershipId, request.Role, cancellationToken);

        return member is null ? NotFound() : Ok(ToResponse(member));
    }

    /// <summary>
    /// Stops one member's access. Their next request is refused; the row stays, marked inactive.
    /// </summary>
    [HttpPost("{membershipId:int}/revoke")]
    public async Task<ActionResult<BusinessMemberResponse>> Revoke(
        int membershipId,
        CancellationToken cancellationToken)
    {
        var member = await _revokeMember.Handle(membershipId, cancellationToken);

        return member is null ? NotFound() : Ok(ToResponse(member));
    }

    /// <summary>
    /// Gives a revoked membership back, with the role it still holds.
    /// </summary>
    [HttpPost("{membershipId:int}/reinstate")]
    public async Task<ActionResult<BusinessMemberResponse>> Reinstate(
        int membershipId,
        CancellationToken cancellationToken)
    {
        var member = await _reinstateMember.Handle(membershipId, cancellationToken);

        return member is null ? NotFound() : Ok(ToResponse(member));
    }

    private static BusinessMemberResponse ToResponse(BusinessMember member) =>
        new(
            member.MembershipId,
            member.DisplayName,
            member.Email,
            member.Role?.ToString(),
            member.IsActive,
            member.JoinedAtUtc);
}
