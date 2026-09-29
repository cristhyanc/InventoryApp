using Inventory.Application.PickList;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/pick-list")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class PickListController : ControllerBase
{
    private readonly GetPickList _getPickList;

    public PickListController(GetPickList getPickList)
    {
        _getPickList = getPickList;
    }

    /// <summary>
    /// The read-only restock-planning projection for the given machines (issue #221): current
    /// machine quantity, target/capacity, and quantity to pick per product/machine, the product total
    /// to pick across the selected machines, and physical storage quantity/shortage. It never creates
    /// an inventory movement, refill, or persisted planning state.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PickListResult>> Get([FromQuery] long[] machineIds, CancellationToken cancellationToken)
    {
        if (machineIds is null || machineIds.Length == 0)
            return BadRequest("At least one machineId is required.");

        var result = await _getPickList.Handle(machineIds, cancellationToken);
        return Ok(result);
    }
}
