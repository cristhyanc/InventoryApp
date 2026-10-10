using Inventory.Application.Businesses;
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
/// Until issue #502 introduces role policies it carries exactly the requirements every other
/// business endpoint carries: authentication, the delegated <c>access_as_user</c> scope, and the
/// membership check <c>BusinessScopeMiddleware</c> applies before any action runs.
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
    public async Task<ActionResult<CurrentBusinessResponse>> Current(CancellationToken cancellationToken)
    {
        var business = await _getCurrentBusiness.Handle(cancellationToken);
        return Ok(new CurrentBusinessResponse(business.Name, business.TimeZoneId));
    }
}
