using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// Route is explicit (not derived from the controller name) so it stays "api/receipts": the
// existing, bookmarked API route for the Purchase business record. See docs/architecture.md's
// Purchase rename plan.
[ApiController]
[Route("api/receipts")]
[Authorize]
[RequiredScope("access_as_user")]
public class PurchasesController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.IPurchaseService _service;

    public PurchasesController(InventoryApi.Services.Interfaces.IPurchaseService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PurchaseResponseDto>>> GetAll([FromQuery] int? supplierId)
    {
        var purchases = await _service.GetAll(supplierId);
        var result = purchases.Select(r => new PurchaseResponseDto(r, _service.ComputeValidation(r)));
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PurchaseResponseDto>> Get(int id)
    {
        var purchase = await _service.Get(id);
        if (purchase is null) return NotFound();
        var validation = _service.ComputeValidation(purchase);
        return Ok(new PurchaseResponseDto(purchase, validation));
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
    public async Task<ActionResult<PurchaseResponseDto>> Upload(
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
        Purchase? purchase;
        try
        {
            purchase = await _service.Upload(file, title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId,
                ParseItems(items));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (purchase is null) return BadRequest("Invalid file or supplier");
        var validation = _service.ComputeValidation(purchase);
        return CreatedAtAction(nameof(Get), new { id = purchase.Id }, new PurchaseResponseDto(purchase, validation));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PurchaseResponseDto>> Update(
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
        Purchase? purchase;
        try
        {
            purchase = await _service.Update(id, title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId,
                ParseItems(items));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (purchase is null) return NotFound();
        var validation = _service.ComputeValidation(purchase);
        return Ok(new PurchaseResponseDto(purchase, validation));
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

    private static IReadOnlyList<PurchaseItemDto>? ParseItems(string? items) =>
        string.IsNullOrWhiteSpace(items) ? Array.Empty<PurchaseItemDto>() :
        System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<PurchaseItemDto>>(
            items,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
}
