using InventoryApi.DTOs;
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
    private readonly INayaxMachineStockSyncService _stockSyncService;

    public MachinesController(IMachineService service, INayaxMachineStockSyncService stockSyncService)
    {
        _service = service;
        _stockSyncService = stockSyncService;
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
        var preview = await _stockSyncService.SyncAsync(id, ct);
        return Ok(preview);
    }

    // Applies only the explicitly accepted events from the preview. Unresolved/needs-review/
    // insufficient-stock events are left untouched even when included in the request.
    [HttpPost("{id:long}/sync-restock/apply")]
    public async Task<ActionResult<NayaxMachineStockApplyResponseDto>> ApplySyncRestock(
        long id, NayaxStockEventApplyRequestDto dto, CancellationToken ct)
    {
        var result = await _stockSyncService.ApplyAsync(id, dto.EventIds, ct);
        return Ok(result);
    }
}
