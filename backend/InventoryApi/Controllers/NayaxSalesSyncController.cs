using Inventory.Application.Access;
using Inventory.Application.SalesSync;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The explicit, shared latest-Nayax-sales synchronization endpoint (issue #187). The home
// dashboard calls this once before loading Sites and Machines so both sections calculate their
// figures from the same freshness boundary, instead of Machines importing the latest transactions
// as a side effect of MachineService.GetAll(). The controller is a thin adapter over the
// Inventory.Application.SalesSync.SyncLatestNayaxSales use case: it binds nothing but the request's
// cancellation token and maps the completed use case to 204.
//
// Authorization: Dashboard.View (issue #526), which every role holds. This endpoint has no page of
// its own - it is the first call the home Dashboard makes on load - so it follows the capability of
// the page that calls it, exactly as the issue directs. Giving it Imports.Run instead would leave
// an Operator's Dashboard refreshing nothing and showing the "sales may be stale" state on every
// visit, which is the page being broken for them rather than a decision anybody made. It imports no
// figure the Dashboard itself does not already show; redacting those figures by role is issue #527.
[ApiController]
[Route("api/nayax-sales-sync")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DashboardView)]
public class NayaxSalesSyncController : ControllerBase
{
    private readonly SyncLatestNayaxSales _syncLatestSales;

    public NayaxSalesSyncController(SyncLatestNayaxSales syncLatestSales)
    {
        _syncLatestSales = syncLatestSales;
    }

    [HttpPost]
    public async Task<IActionResult> SyncLatest(CancellationToken ct)
    {
        await _syncLatestSales.Handle(ct);
        return NoContent();
    }
}
