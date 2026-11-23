using Inventory.Application.Members;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// Redeeming an invite link (issue #532, one slice of #504 under #328): the endpoint a signed-in
/// person who belongs to no business calls to join one.
///
/// <para><strong>Why this is not an action on <see cref="MemberInvitesController"/>.</strong> That
/// controller is the Owner's invite management and declares <c>Members.Manage</c> on itself, a
/// capability the person redeeming an invite could not possibly hold - they have no membership and
/// therefore no role. This endpoint has the opposite authorization shape, so it is its own
/// controller with its own story rather than an exception inside somebody else's.</para>
///
/// <para><strong>The authorization story, in full.</strong> The action carries
/// <see cref="MembershipNotRequiredEndpointAttribute"/> (issue #523) and is named on that marker's
/// reviewed allowlist in <c>EndpointAuthorizationClassificationTests</c>, which is class 3 of the
/// four ways an endpoint may be authorized. It still requires authentication and the delegated
/// <c>access_as_user</c> scope - "membership not required" is not "anonymous" - and it still runs
/// with a <em>denied</em> <c>BusinessScope</c>: nothing resolves a business for the request, the
/// tenant query filters and <c>BusinessOwnershipEnforcer</c> stay fully in force, and it reads no
/// business data. Two of the three tables it does reach, invites and memberships, are structurally
/// non-tenant-owned; the third is the acceptance record of issue #1259, which is tenant-owned and
/// written through the narrow, reviewed window of <c>AppDbContext.BeginLegalAcceptanceWrite</c>. The
/// business id in every statement comes from the invite row the presented token identified
/// (docs/architecture.md § Invite redemption).</para>
///
/// <para><strong>Nothing in the request chooses anything.</strong> The body carries the token and
/// the version of the Terms the page showed, and nothing else: the business, the role and the person
/// are all decided by the server, from the invite row and the validated token the request
/// authenticated with, and which version of the Terms is <em>current</em> is the server's constant -
/// the body's version is only compared against it (issue #1259).</para>
///
/// <para><strong>Refusals are mapped centrally and are two sentences.</strong> An unusable invite -
/// expired, used, cancelled, unknown, of a business that is not active, or granting an undeclared
/// role - throws <c>DomainValidationException</c>, which <see cref="Http.DomainExceptionHandler"/>
/// answers as <c>400</c> with the Domain's one generic sentence, so none of those cases can be told
/// apart and none reveals a business. A caller who already holds an active membership throws
/// <c>DomainConflictException</c> and is answered as <c>409 Conflict</c>. No response here carries a
/// token, a business name or a business id.</para>
/// </summary>
[ApiController]
[Route("api/invites")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class InviteRedemptionController : ControllerBase
{
    private readonly RedeemMemberInvite _redeemInvite;

    public InviteRedemptionController(RedeemMemberInvite redeemInvite)
    {
        _redeemInvite = redeemInvite;
    }

    /// <summary>
    /// Joins the business that issued the submitted invite, with the invite's role, closes the
    /// invite, and records the joining person's acceptance of the published Terms of Use and Privacy
    /// Policy (issue #1259) - all in one transaction, so a redemption either grants the membership,
    /// records the link as used and records the agreement, or does none of them.
    /// </summary>
    [HttpPost("redeem")]
    [MembershipNotRequiredEndpoint]
    public async Task<ActionResult<RedeemedMemberInviteResponse>> Redeem(
        RedeemMemberInviteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var redeemed = await _redeemInvite.Handle(
            request.Token,
            request.AcceptedLegalVersion,
            cancellationToken);

        return Ok(new RedeemedMemberInviteResponse(redeemed.Role.ToString()));
    }
}
