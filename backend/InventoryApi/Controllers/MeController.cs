using Inventory.Application.Access;
using InventoryApi.Auth;
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
/// <c>GET access</c> carries exactly the requirements every other business endpoint carries:
/// authentication, the delegated <c>access_as_user</c> scope, and the membership check
/// <c>BusinessScopeMiddleware</c> applies before any action runs. Deliberately no capability
/// requirement of its own: a member has to be able to ask what they may do, whatever role they
/// hold. Enforcing capabilities on the other endpoints is issue #502's work.
///
/// <c>GET account-state</c> (issue #523) is the one action here that a person without a usable
/// membership may reach, through <see cref="MembershipNotRequiredEndpointAttribute"/>. That
/// exemption is per action and is not a property of this controller: every other action on it,
/// now and in future, keeps the membership requirement, and the marked action still requires
/// authentication and the same delegated scope and still runs with a denied business scope.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class MeController : ControllerBase
{
    private readonly GetCurrentAccess _getCurrentAccess;
    private readonly GetAccountState _getAccountState;

    public MeController(GetCurrentAccess getCurrentAccess, GetAccountState getAccountState)
    {
        _getCurrentAccess = getCurrentAccess;
        _getAccountState = getAccountState;
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

    /// <summary>
    /// Which state the caller's own account is in (issue #523), so a person the other endpoints
    /// refuse can be shown the screen that explains it rather than a bare 403.
    ///
    /// It takes no parameter, describes the caller alone, and names no business: the states are
    /// about the caller's membership, and the middleware's public 403 body is unchanged by this
    /// endpoint existing. The state is reported as its published name; <c>onboardingEnabled</c>
    /// and <c>rejectionReason</c> are decided by the use case.
    /// </summary>
    [HttpGet("account-state")]
    [MembershipNotRequiredEndpoint]
    public async Task<ActionResult<MeAccountStateResponse>> AccountState(CancellationToken cancellationToken)
    {
        var accountState = await _getAccountState.Handle(cancellationToken);

        return Ok(new MeAccountStateResponse(
            accountState.State.ToString(),
            accountState.OnboardingEnabled,
            accountState.RejectionReason));
    }
}
