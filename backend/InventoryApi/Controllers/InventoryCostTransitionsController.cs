using Inventory.Application.Costing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// Each action calls its Inventory.Application.Costing use case directly (issue #298). Their
// deliberate validation checks throw DomainValidationException, which DomainExceptionHandler (see
// Http/DomainExceptionHandler.cs) maps centrally to a 400 ProblemDetails carrying the same message
// these actions used to return directly, so none of them need their own catch block. A Nayax
// upstream failure reaches NayaxUpstreamExceptionHandler, and an unexpected framework exception is
// deliberately not mapped: it reaches GlobalExceptionHandler as a logged, generic 500.
[ApiController]
[Route("api/admin/inventory-cost-transition")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class InventoryCostTransitionsController : ControllerBase
{
    private readonly PreviewInventoryCostTransition _preview;
    private readonly ApplyInventoryCostTransition _apply;
    private readonly PreviewAllInventoryCostTransitions _previewAll;
    private readonly ApplyAllInventoryCostTransitions _applyAll;

    public InventoryCostTransitionsController(
        PreviewInventoryCostTransition preview,
        ApplyInventoryCostTransition apply,
        PreviewAllInventoryCostTransitions previewAll,
        ApplyAllInventoryCostTransitions applyAll)
    {
        _preview = preview;
        _apply = apply;
        _previewAll = previewAll;
        _applyAll = applyAll;
    }

    [HttpPost("preview")]
    public async Task<ActionResult<InventoryCostTransitionPreview>> Preview(
        [FromBody] InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _preview.Handle(request, cancellationToken));

    [HttpPost("apply")]
    public async Task<ActionResult<InventoryCostTransitionPreview>> Apply(
        [FromBody] ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _apply.Handle(request, cancellationToken));

    [HttpPost("preview-all")]
    public async Task<ActionResult<InventoryCostTransitionBatchPreview>> PreviewAll(
        [FromBody] InventoryCostTransitionBatchPreviewRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _previewAll.Handle(request, cancellationToken));

    [HttpPost("apply-all")]
    public async Task<ActionResult<InventoryCostTransitionBatchPreview>> ApplyAll(
        [FromBody] ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _applyAll.Handle(request, cancellationToken));
}
