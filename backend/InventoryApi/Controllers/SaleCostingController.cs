using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/sale-costing")]
public sealed class SaleCostingController : ControllerBase
{
    private readonly ISaleCostingService _service;

    public SaleCostingController(ISaleCostingService service) => _service = service;

    [HttpPost("backfill")]
    public async Task<ActionResult<SaleCostingBackfillResult>> Backfill(
        [FromQuery] bool dryRun = true,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!dryRun && !force)
            return BadRequest("A non-dry-run backfill requires force=true.");

        return Ok(await _service.BackfillAsync(dryRun, force, cancellationToken));
    }
}
