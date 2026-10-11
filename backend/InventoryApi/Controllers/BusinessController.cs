using Inventory.Application.Access;
using Inventory.Application.Businesses;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The signed-in operator's own business (issue #499).
///
/// It exists so the frontend can render operator-facing instants and resolve calendar inputs in
/// the business's own time zone instead of a hard-coded constant. There is no route, query or body
/// parameter: the business comes from the authenticated actor's membership, through the issue #64
/// current-business abstraction, so no request value can choose whose business is described.
///
/// On top of the requirements every business endpoint carries - authentication, the delegated
/// <c>access_as_user</c> scope and the membership check <c>BusinessScopeMiddleware</c> applies
/// before any action runs - <c>GET current</c> requires <c>Dashboard.View</c> (issue #525). Every
/// role holds it: the name and time zone this endpoint reports are what renders the application's
/// own shell, so anybody who may see a page needs them. The capability is declared on the action
/// rather than on the controller because changing the business record is <c>Business.Manage</c>
/// (issue #526), and that endpoint must not inherit this one's rule.
/// </summary>
[ApiController]
[Route("api/business")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class BusinessController : ControllerBase
{
    private readonly GetCurrentBusiness _getCurrentBusiness;

    public BusinessController(GetCurrentBusiness getCurrentBusiness)
    {
        _getCurrentBusiness = getCurrentBusiness;
    }

    [HttpGet("current")]
    [RequireCapability(Capability.DashboardView)]
    public async Task<ActionResult<CurrentBusinessResponse>> Current(CancellationToken cancellationToken)
    {
        var business = await _getCurrentBusiness.Handle(cancellationToken);
        return Ok(new CurrentBusinessResponse(business.Name, business.TimeZoneId));
    }
}
