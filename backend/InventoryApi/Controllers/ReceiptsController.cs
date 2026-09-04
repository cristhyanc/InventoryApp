using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.DTOs;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReceiptsController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.IReceiptService _service;

    public ReceiptsController(InventoryApi.Services.Interfaces.IReceiptService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Receipt>>> GetAll([FromQuery] int? supplierId)
    {
        var receipts = await _service.GetAll(supplierId);
        return Ok(receipts);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Receipt>> Get(int id)
    {
        var receipt = await _service.Get(id);
        return receipt is null ? NotFound() : Ok(receipt);
    }

    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> GetFile(int id)
    {
        var (content, contentType, fileName) = await _service.GetFile(id);
        if (content is null) return NotFound();
        return File(content, contentType ?? "application/octet-stream", fileName);
    }

    // multipart/form-data: file + title + notes + totalAmount + deliveryCost + packageCost + purchaseDate + supplierId
    [HttpPost]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<Receipt>> Upload(
        [FromForm] IFormFile file,
        [FromForm] string title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] decimal? deliveryCost,
        [FromForm] decimal? packageCost,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId,
        [FromForm] string? items)
    {
        Receipt? receipt;
        try
        {
            receipt = await _service.Upload(file, title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId,
                ParseItems(items));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (receipt is null) return BadRequest("Invalid file or supplier");
        return CreatedAtAction(nameof(Get), new { id = receipt.Id }, receipt);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Receipt>> Update(
        int id,
        [FromForm] string? title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] decimal? deliveryCost,
        [FromForm] decimal? packageCost,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId,
        [FromForm] string? items)
    {
        Receipt? receipt;
        try
        {
            receipt = await _service.Update(id, title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId,
                ParseItems(items));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (receipt is null) return NotFound();
        return Ok(receipt);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.Delete(id);
        return ok ? NoContent() : NotFound();
    }

    private static IReadOnlyList<ReceiptItemDto>? ParseItems(string? items) =>
        string.IsNullOrWhiteSpace(items) ? Array.Empty<ReceiptItemDto>() :
        System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<ReceiptItemDto>>(items);
}
