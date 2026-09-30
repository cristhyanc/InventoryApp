using Inventory.Application.InventoryCounting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Take Inventory page's Apply action (issue #245): confirms/applies one product's physical
/// storage count. <see cref="ApplyInventoryCount"/> re-reads the authoritative current quantity and
/// throws a <c>DomainConflictException</c> (mapped centrally to 409) when it no longer matches what
/// the operator counted against, and a <c>DomainValidationException</c> (mapped centrally to 400)
/// for an invalid count or a missing restock-cost suggestion.
/// </summary>
[ApiController]
[Route("api/products/{productId:long}/inventory-count")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class InventoryCountController : ControllerBase
{
    private readonly ApplyInventoryCount _applyInventoryCount;

    public InventoryCountController(ApplyInventoryCount applyInventoryCount)
    {
        _applyInventoryCount = applyInventoryCount;
    }

    [HttpPost("apply")]
    public async Task<ActionResult<InventoryCountApplyResultDto>> Apply(
        long productId, InventoryCountApplyRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _applyInventoryCount.Handle(productId, request, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
