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
public class SupplierOrdersController : ControllerBase
{
    private readonly ISupplierOrderService _service;

    public SupplierOrdersController(ISupplierOrderService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SupplierOrder>>> GetActive() => Ok(await _service.GetActive());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierOrder>> GetById(int id)
    {
        var order = await _service.GetById(id);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpPost]
    public async Task<ActionResult<SupplierOrder>> Create(SupplierOrderCreateDto dto)
    {
        // Validation failures (InvalidOperationException) are mapped centrally by
        // DomainExceptionHandler (see Http/DomainExceptionHandler.cs) into a 400 ProblemDetails
        // with the same message this action used to return directly.
        var order = await _service.Create(dto);
        return order is null ? BadRequest("Invalid supplier.") : CreatedAtAction(nameof(GetActive), new { id = order.Id }, order);
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id) => await _service.Cancel(id) ? NoContent() : NotFound();
}
