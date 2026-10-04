using Inventory.Application.SupplierOrders;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
public class SupplierOrdersController : ControllerBase
{
    private readonly ListActiveSupplierOrders _listActiveSupplierOrders;
    private readonly GetSupplierOrder _getSupplierOrder;
    private readonly CreateSupplierOrder _createSupplierOrder;
    private readonly CancelSupplierOrder _cancelSupplierOrder;

    public SupplierOrdersController(
        ListActiveSupplierOrders listActiveSupplierOrders,
        GetSupplierOrder getSupplierOrder,
        CreateSupplierOrder createSupplierOrder,
        CancelSupplierOrder cancelSupplierOrder)
    {
        _listActiveSupplierOrders = listActiveSupplierOrders;
        _getSupplierOrder = getSupplierOrder;
        _createSupplierOrder = createSupplierOrder;
        _cancelSupplierOrder = cancelSupplierOrder;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SupplierOrderResponse>>> GetActive()
    {
        var orders = await _listActiveSupplierOrders.Handle(CancellationToken.None);
        return Ok(orders.Select(SupplierOrderResponseMapper.ToResponse).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierOrderResponse>> GetById(int id)
    {
        var order = await _getSupplierOrder.Handle(id, CancellationToken.None);
        return order is null ? NotFound() : Ok(SupplierOrderResponseMapper.ToResponse(order));
    }

    [HttpPost]
    public async Task<ActionResult<SupplierOrderResponse>> Create(SupplierOrderCreateDto dto)
    {
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
            : CreatedAtAction(nameof(GetActive), new { id = order.Id }, SupplierOrderResponseMapper.ToResponse(order));
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id) =>
        await _cancelSupplierOrder.Handle(id, CancellationToken.None) ? NoContent() : NotFound();
}
