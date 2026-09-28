using Inventory.Application.MachineStockSync;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
public class MachinesController : ControllerBase
{
    private readonly IMachineService _service;
    private readonly SyncMachineStockFromNayax _syncMachineStock;
    private readonly ApplyMachineStockSync _applyMachineStockSync;
    private readonly ResolveMachineStockDuplicate _resolveMachineStockDuplicate;

    public MachinesController(
        IMachineService service,
        SyncMachineStockFromNayax syncMachineStock,
        ApplyMachineStockSync applyMachineStockSync,
        ResolveMachineStockDuplicate resolveMachineStockDuplicate)
    {
        _service = service;
        _syncMachineStock = syncMachineStock;
        _applyMachineStockSync = applyMachineStockSync;
        _resolveMachineStockDuplicate = resolveMachineStockDuplicate;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<Machine>> GetById(long id)
    {
        var machine = await _service.GetById(id);
        return machine is null ? NotFound() : Ok(machine);
    }

    [HttpGet]
    public async Task<ActionResult<List<Machine>>> GetAll()
    {
        var machines = await _service.GetAll();
        return Ok(machines);
    }

    [HttpGet("{id:long}/products")]
    public async Task<ActionResult<List<Product>>> GetMachineProducts(long id)
    {
        var products = await _service.GetMachineProducts(id);
        return Ok(products);
    }

    // Fetches new Nayax stock-adjustment alerts and returns a reconciliation preview; it never
    // changes storage inventory itself (issue #183).
    [HttpPost("{id:long}/sync-restock")]
    public async Task<ActionResult<NayaxMachineStockSyncPreviewDto>> SyncRestock(long id, CancellationToken ct)
    {
        var preview = await _syncMachineStock.Handle(id, ct);
        return Ok(preview);
    }

    // Applies only the explicitly accepted events from the preview. Unresolved/needs-review/
    // insufficient-stock events are left untouched even when included in the request. A flagged
    // possible duplicate is never applied here: it must go through resolve-duplicate first
    // (issue #196).
    [HttpPost("{id:long}/sync-restock/apply")]
    public async Task<ActionResult<NayaxMachineStockApplyResponseDto>> ApplySyncRestock(
        long id, NayaxStockEventApplyRequestDto dto, CancellationToken ct)
    {
        var result = await _applyMachineStockSync.Handle(id, dto.EventIds, ct);
        return Ok(result);
    }

    // The explicit resolution for a Nayax event flagged as a possible duplicate of a manual refill
    // (issue #196): "already recorded manually" reconciles it without any movement; "apply as
    // separate restock" is an explicit, auditable override that applies it exactly once.
    [HttpPost("{id:long}/sync-restock/resolve-duplicate")]
    public async Task<ActionResult<NayaxStockEventApplyResultDto>> ResolveSyncRestockDuplicate(
        long id, NayaxResolveDuplicateRequestDto dto, CancellationToken ct)
    {
        var result = await _resolveMachineStockDuplicate.Handle(id, dto.EventId, dto.Resolution, ct);
        return Ok(result);
    }
}
