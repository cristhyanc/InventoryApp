using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The explicit, shared latest-Nayax-sales synchronization endpoint (issue #187). The home
// dashboard calls this once before loading Sites and Machines so both sections calculate their
// figures from the same freshness boundary, instead of Machines importing the latest transactions
// as a side effect of MachineService.GetAll().
[ApiController]
[Route("api/nayax-sales-sync")]
[Authorize]
[RequiredScope("access_as_user")]
public class NayaxSalesSyncController : ControllerBase
{
    private readonly INayaxLatestSalesSyncService _service;

    public NayaxSalesSyncController(INayaxLatestSalesSyncService service)
    {
        _service = service;
    }

    [HttpPost]
    public async Task<IActionResult> SyncLatest(CancellationToken ct)
    {
        await _service.SyncLatestSalesAsync(ct);
        return NoContent();
    }
}
