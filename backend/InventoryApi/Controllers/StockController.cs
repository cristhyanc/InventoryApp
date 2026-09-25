using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/products/{productId:long}/stock")]
[Authorize]
[RequiredScope("access_as_user")]
public class StockController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.IStockService _service;
    public StockController(InventoryApi.Services.Interfaces.IStockService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StockAdjustment>>> History(long productId)
    {
        var history = await _service.History(productId);
        if (!history.Any()) return NotFound("Product not found");
        return Ok(history);
    }

    [HttpGet("restock-cost-suggestion")]
    public async Task<ActionResult<RestockCostSuggestionDto>> GetRestockCostSuggestion(long productId)
    {
        var suggestion = await _service.GetRestockCostSuggestion(productId);
        return suggestion is null ? NotFound() : Ok(suggestion);
    }

    [HttpPost]
    public async Task<ActionResult<StockAdjustment>> Adjust(long productId, StockAdjustmentDto dto)
    {
        // InsufficientStockException and DomainValidationException are mapped centrally by
        // DomainExceptionHandler (see Http/DomainExceptionHandler.cs) into a 400 ProblemDetails
        // with the same message this action used to return directly.
        var adjustment = await _service.Adjust(productId, dto);

        if (adjustment is null) return BadRequest("Invalid product or resulting quantity");
        return Ok(adjustment);
    }
}
