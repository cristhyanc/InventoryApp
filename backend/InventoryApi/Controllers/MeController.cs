using Inventory.Application.Access;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The signed-in member's own access (issue #521).
///
/// There is no route, query or body parameter: the member and their business come from the
/// authenticated actor's membership, through the issue #64 current-business abstraction, so no
/// request value can choose whose access is described. It reports the caller and nothing else -
/// no other member, no other business, and no identifier for either.
///
/// It carries exactly the requirements every other business endpoint carries: authentication, the
/// delegated <c>access_as_user</c> scope, and the membership check <c>BusinessScopeMiddleware</c>
/// applies before any action runs. Deliberately no capability requirement of its own: a member has
/// to be able to ask what they may do, whatever role they hold. Enforcing capabilities on the
/// other endpoints is issue #502's work.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class MeController : ControllerBase
{
    private readonly GetCurrentAccess _getCurrentAccess;

    public MeController(GetCurrentAccess getCurrentAccess)
    {
        _getCurrentAccess = getCurrentAccess;
    }

    [HttpGet("access")]
    public async Task<ActionResult<MeAccessResponse>> Access(CancellationToken cancellationToken)
    {
        var access = await _getCurrentAccess.Handle(cancellationToken);

        return Ok(new MeAccessResponse(
            access.Role.ToString(),
            [.. access.Capabilities.Select(Capabilities.Name)],
            access.BusinessName,
            access.TimeZoneId));
    }
}
