using Inventory.Application.Access;
using Inventory.Application.Costing;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The sale-costing backfills: the internal-ledger backfill and the Nayax historical cost backfill.
///
/// Every action requires <c>Data.Repair</c> (issue #526), so an Owner alone. The dry run is as
/// restricted as the apply, and has to be: on both surfaces the only difference between projecting
/// the change and writing it is a flag - <c>dryRun</c> here, a separate route on the Nayax
/// backfill - and what they project is the business's historical COGS.
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
