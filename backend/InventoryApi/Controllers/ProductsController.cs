using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
public class ProductsController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.IProductService _service;

    public ProductsController(InventoryApi.Services.Interfaces.IProductService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Product>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] int? supplierId,
        [FromQuery] bool? lowStockOnly)
    {
        var result = await _service.GetAll(search, categoryId, supplierId, lowStockOnly);
        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<Product>> Get(long id)
    {
        var product = await _service.Get(id);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpGet("alerts/low-stock")]
    public async Task<ActionResult<IEnumerable<Product>>> LowStock(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] int? supplierId)
    {
        var items = await _service.LowStock(search, categoryId, supplierId);
        return Ok(items);
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, ProductUpdateDto dto)
    {
        try
        {
            var ok = await _service.Update(id, dto);
            return ok ? NoContent() : NotFound();
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var ok = await _service.Delete(id);
        return ok ? NoContent() : NotFound();
    }
}
