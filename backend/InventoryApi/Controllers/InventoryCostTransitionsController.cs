using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// Validation failures (InvalidOperationException) are mapped centrally by DomainExceptionHandler
// (see Http/DomainExceptionHandler.cs) into a 400 ProblemDetails with the same message these
// actions used to return directly, so none of them need their own catch block any more.
[ApiController]
[Route("api/admin/inventory-cost-transition")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class InventoryCostTransitionsController : ControllerBase
{
    private readonly IInventoryCostTransitionService _service;

    public InventoryCostTransitionsController(IInventoryCostTransitionService service) => _service = service;

    [HttpPost("preview")]
    public async Task<ActionResult<InventoryCostTransitionPreview>> Preview(
        [FromBody] InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.PreviewAsync(request, cancellationToken));

    [HttpPost("apply")]
    public async Task<ActionResult<InventoryCostTransitionPreview>> Apply(
        [FromBody] ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.ApplyAsync(request, cancellationToken));

    [HttpPost("preview-all")]
    public async Task<ActionResult<InventoryCostTransitionBatchPreview>> PreviewAll(
        [FromBody] InventoryCostTransitionBatchPreviewRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.PreviewAllAsync(request, cancellationToken));

    [HttpPost("apply-all")]
    public async Task<ActionResult<InventoryCostTransitionBatchPreview>> ApplyAll(
        [FromBody] ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.ApplyAllAsync(request, cancellationToken));
}
