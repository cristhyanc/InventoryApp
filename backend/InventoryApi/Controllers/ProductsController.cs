using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
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
    public async Task<ActionResult<IEnumerable<Product>>> LowStock()
    {
        var items = await _service.LowStock();
        return Ok(items);
    }

    [HttpPost("importProducts")]
    public async Task<ActionResult<bool>> ImportProducts()
    {
        var result = await _service.ImportProductsAsync();
        return result;
    }

    [HttpPost]
    public async Task<ActionResult<Product>> Create(ProductCreateDto dto)
    {
        var product = await _service.Create(dto);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, ProductUpdateDto dto)
    {
        var ok = await _service.Update(id, dto);
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var ok = await _service.Delete(id);
        return ok ? NoContent() : NotFound();
    }
}
