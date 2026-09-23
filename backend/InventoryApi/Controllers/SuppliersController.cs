using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
public class SuppliersController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.ISupplierService _service;
    public SuppliersController(InventoryApi.Services.Interfaces.ISupplierService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetAll() => Ok(await _service.GetAll());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Supplier>> Get(int id)
    {
        var supplier = await _service.Get(id);
        return supplier is null ? NotFound() : Ok(supplier);
    }

    [HttpPost]
    public async Task<ActionResult<Supplier>> Create(SupplierDto dto)
    {
        var supplier = await _service.Create(dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address);
        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, supplier);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SupplierDto dto)
    {
        var ok = await _service.Update(id, dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address);
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.Delete(id);
        return ok ? NoContent() : NotFound();
    }
}
