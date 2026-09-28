using Inventory.Application.SalesSync;
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
[ApiController]
[Route("api/nayax-sales-sync")]
[Authorize]
[RequiredScope("access_as_user")]
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
