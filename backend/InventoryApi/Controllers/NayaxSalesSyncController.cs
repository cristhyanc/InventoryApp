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
// It requires Dashboard.View (issue #526), which every role holds. Issue #526 says this endpoint
// follows the page that calls it, and that page is the home Dashboard, which calls it on load:
// putting it on Imports.Run instead would leave an Operator's Dashboard refreshing nothing and
// reporting stale sales on every visit - the page broken for them rather than a decision anybody
// made. It surfaces no figure the Dashboard does not already show, and redacting those figures by
// role is issue #527.
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
