using Inventory.Application.Access;
using Inventory.Application.PickList;
using Inventory.Domain.Stock;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The pick list: the read-only restock-planning projection, and the one write action that records
/// what a worker actually took (issue #743).
///
/// <para>The read declares <c>PickList.View</c> on the controller. The refill declares
/// <c>Inventory.Operate</c> on the action as well, because recording a stock movement is day-to-day
/// inventory work and that is the capability every other stock write declares; requirements
/// accumulate down an endpoint, so the refill needs both and no role's access to the read changes
/// (docs/architecture.md § Capability authorization on the Manager and Owner endpoints). Every role
/// that holds <c>Inventory.Operate</c> today also holds <c>PickList.View</c>. Being a write, it is
/// also refused in a read-only support session by the central <c>GET</c>/<c>HEAD</c> rule of issue
/// #551.</para>
/// </summary>
[ApiController]
[Route("api/pick-list")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.PickListView)]
public sealed class PickListController : ControllerBase
{
    private readonly GetPickList _getPickList;
    private readonly RecordPickListRefill _recordRefill;

    public PickListController(GetPickList getPickList, RecordPickListRefill recordRefill)
    {
        _getPickList = getPickList;
        _recordRefill = recordRefill;
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

    /// <summary>
    /// Records everything taken out of storage for a pick as one <c>MachineRefill</c> movement per
    /// row, in one transaction: all rows or none (issue #743, parent #329 child H1).
    ///
    /// <para><c>200 OK</c> with the recorded refill, whether this request recorded it or a previous
    /// request under the same idempotency key already did - the response says which through
    /// <see cref="PickListRefillResponse.Replayed"/>. <c>400 Bad Request</c> with a per-row problem
    /// document when anything was refused, having written nothing. <c>409 Conflict</c> when the key
    /// has already recorded a <em>different</em> refill, mapped centrally by
    /// <c>DomainExceptionHandler</c> from the <c>DomainConflictException</c> the use case raises.</para>
    /// </summary>
    [HttpPost("refills")]
    [RequireCapability(Capability.InventoryOperate)]
    public async Task<ActionResult<PickListRefillResponse>> RecordRefill(
        PickListRefillRequest request, CancellationToken cancellationToken)
    {
        // A missing element is passed on as a missing row rather than read: JSON can put null in an
        // array whatever the element type says, and the row rules - including the one that refuses
        // an absent row on its own line - are the policy's, not this boundary's.
        List<PickListRefillRequestRow?>? rows = request?.Rows is null
            ? null
            : [.. request.Rows.Select(row =>
                row is null ? null : new PickListRefillRequestRow(row.ProductId, row.QuantityTaken))];

        var attempt = await _recordRefill.Handle(
            new PickListRefillInput(request?.IdempotencyKey, request?.MachineId, request?.Notes, rows),
            cancellationToken);

        if (attempt.Outcome == PickListRefillOutcome.Rejected)
        {
            return new ObjectResult(PickListRefillResponseMapper.ToProblem(attempt.Rejection!))
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" },
            };
        }

        return Ok(PickListRefillResponseMapper.ToResponse(
            attempt.Record!, replayed: attempt.Outcome == PickListRefillOutcome.Replayed));
    }
}
