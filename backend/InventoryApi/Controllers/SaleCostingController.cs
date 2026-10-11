using Inventory.Application.Access;
using Inventory.Application.Costing;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The sale-costing backfills: the maintenance runs that fill in or recompute the cost recorded
/// against past sales.
///
/// The whole controller requires <c>Data.Repair</c> (issue #526), which only an Owner holds. A
/// dry run carries the same capability as the apply it precedes: it reads the same historical cost
/// ledger, and the only difference between the two is a query-string flag.
/// </summary>
[ApiController]
[Route("api/sale-costing")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DataRepair)]
public sealed class SaleCostingController : ControllerBase
{
    private readonly BackfillSaleCosts _backfill;
    private readonly BackfillNayaxHistoricalSaleCosts _nayaxCostBackfill;

    public SaleCostingController(BackfillSaleCosts backfill, BackfillNayaxHistoricalSaleCosts nayaxCostBackfill)
    {
        _backfill = backfill;
        _nayaxCostBackfill = nayaxCostBackfill;
    }

    [HttpPost("backfill")]
    public async Task<ActionResult<SaleCostingBackfillResult>> Backfill(
        [FromQuery] bool dryRun = true,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!dryRun && !force)
            return BadRequest("A non-dry-run backfill requires force=true.");

        return Ok(await _backfill.Handle(dryRun, force, cancellationToken));
    }

    [HttpPost("nayax-cost-backfill/dry-run")]
    public async Task<ActionResult<NayaxCostBackfillResult>> DryRunNayaxCostBackfill(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] long? productId = null,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default) =>
        Ok(await _nayaxCostBackfill.Handle(
            dryRun: true, force: force, from: from, to: to, productId: productId,
            cancellationToken: cancellationToken));

    [HttpPost("nayax-cost-backfill/apply")]
    public async Task<ActionResult<NayaxCostBackfillResult>> ApplyNayaxCostBackfill(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] long? productId = null,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default) =>
        Ok(await _nayaxCostBackfill.Handle(
            dryRun: false, force: force, from: from, to: to, productId: productId,
            cancellationToken: cancellationToken));
}
