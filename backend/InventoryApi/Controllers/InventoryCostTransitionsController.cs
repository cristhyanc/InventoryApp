using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/admin/inventory-cost-transition")]
public sealed class InventoryCostTransitionsController : ControllerBase
{
    private readonly IInventoryCostTransitionService _service;

    public InventoryCostTransitionsController(IInventoryCostTransitionService service) => _service = service;

    [HttpPost("preview")]
    public async Task<ActionResult<InventoryCostTransitionPreview>> Preview(
        [FromBody] InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.PreviewAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("apply")]
    public async Task<ActionResult<InventoryCostTransitionPreview>> Apply(
        [FromBody] ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ApplyAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("preview-all")]
    public async Task<ActionResult<InventoryCostTransitionBatchPreview>> PreviewAll(
        [FromBody] InventoryCostTransitionBatchPreviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.PreviewAllAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("apply-all")]
    public async Task<ActionResult<InventoryCostTransitionBatchPreview>> ApplyAll(
        [FromBody] ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ApplyAllAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
