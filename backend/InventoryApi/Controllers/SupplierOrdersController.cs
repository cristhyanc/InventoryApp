using Inventory.Application.Access;
using Inventory.Application.SupplierOrders;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// Outstanding supplier orders: what has been ordered and not yet received. Day-to-day purchasing
/// work, so every action requires <c>Purchasing.Operate</c> (issue #525).
///
/// <para><b>A line's nested catalogue product carries no <c>inventoryValue</c> for a caller without
/// <c>Dashboard.ViewFinancials</c></b> (issue #700), for the reason it does not on
/// <see cref="PurchasesController"/>: that nested object is the same catalogue snapshot
/// <c>api/products</c> serves, and issue #698's redaction there meant nothing while listing the
/// outstanding orders returned the same figure. What is ordered, at what unit price, and how much
/// is still outstanding stays visible to every role. The decision is made once per request from
/// <see cref="CurrentCapabilities"/> and never from a role name.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.PurchasingOperate)]
public class SupplierOrdersController : ControllerBase
{
    private readonly ListActiveSupplierOrders _listActiveSupplierOrders;
    private readonly GetSupplierOrder _getSupplierOrder;
    private readonly CreateSupplierOrder _createSupplierOrder;
    private readonly CancelSupplierOrder _cancelSupplierOrder;
    private readonly CurrentCapabilities _capabilities;

    public SupplierOrdersController(
        ListActiveSupplierOrders listActiveSupplierOrders,
        GetSupplierOrder getSupplierOrder,
        CreateSupplierOrder createSupplierOrder,
        CancelSupplierOrder cancelSupplierOrder,
        CurrentCapabilities capabilities)
    {
        _listActiveSupplierOrders = listActiveSupplierOrders;
        _getSupplierOrder = getSupplierOrder;
        _createSupplierOrder = createSupplierOrder;
        _cancelSupplierOrder = cancelSupplierOrder;
        _capabilities = capabilities;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SupplierOrderResponse>>> GetActive()
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var orders = await _listActiveSupplierOrders.Handle(CancellationToken.None);
        return Ok(orders.Select(order => SupplierOrderResponseMapper.ToResponse(order, visibility)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierOrderResponse>> GetById(int id)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var order = await _getSupplierOrder.Handle(id, CancellationToken.None);
        return order is null ? NotFound() : Ok(SupplierOrderResponseMapper.ToResponse(order, visibility));
    }

    [HttpPost]
    public async Task<ActionResult<SupplierOrderResponse>> Create(SupplierOrderCreateDto dto)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);

        // The use case's deliberate validation checks throw DomainValidationException, which
        // DomainExceptionHandler (see Http/DomainExceptionHandler.cs) maps centrally to a 400
        // ProblemDetails carrying the same message this action used to return directly.
        var order = await _createSupplierOrder.Handle(
            new SupplierOrderCreateFields(
                dto.SupplierId,
                dto.OrderDate,
                dto.ExpectedDate,
                dto.Reference,
                dto.Notes,
                dto.Lines.Select(line => new SupplierOrderLineInput(line.ProductId, line.QuantityOrdered, line.UnitPrice, line.Notes)).ToList()),
            CancellationToken.None);

        return order is null
            ? BadRequest("Invalid supplier.")
            : CreatedAtAction(
                nameof(GetActive),
                new { id = order.Id },
                SupplierOrderResponseMapper.ToResponse(order, visibility));
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id) =>
        await _cancelSupplierOrder.Handle(id, CancellationToken.None) ? NoContent() : NotFound();
}
