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
public class ReceiptsController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.IReceiptService _service;

    public ReceiptsController(InventoryApi.Services.Interfaces.IReceiptService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ReceiptResponseDto>>> GetAll([FromQuery] int? supplierId)
    {
        var receipts = await _service.GetAll(supplierId);
        var result = receipts.Select(r => new ReceiptResponseDto(r, _service.ComputeValidation(r)));
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ReceiptResponseDto>> Get(int id)
    {
        var receipt = await _service.Get(id);
        if (receipt is null) return NotFound();
        var validation = _service.ComputeValidation(receipt);
        return Ok(new ReceiptResponseDto(receipt, validation));
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
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<ReceiptResponseDto>> Upload(
        IFormFile file,
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
        var validation = _service.ComputeValidation(receipt);
        return CreatedAtAction(nameof(Get), new { id = receipt.Id }, new ReceiptResponseDto(receipt, validation));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ReceiptResponseDto>> Update(
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
        var validation = _service.ComputeValidation(receipt);
        return Ok(new ReceiptResponseDto(receipt, validation));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var ok = await _service.Delete(id);
            return ok ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private static IReadOnlyList<ReceiptItemDto>? ParseItems(string? items) =>
        string.IsNullOrWhiteSpace(items) ? Array.Empty<ReceiptItemDto>() :
        System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<ReceiptItemDto>>(
            items,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
}
