using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.DTOs;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/products/{productId:long}/stock")]
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
        StockAdjustment? adjustment;
        try
        {
            adjustment = await _service.Adjust(productId, dto);
        }
        catch (InventoryApi.Services.InsufficientStockException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }

        if (adjustment is null) return BadRequest("Invalid product or resulting quantity");
        return Ok(adjustment);
    }
}
