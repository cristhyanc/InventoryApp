using Inventory.Application.Stock;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/products/{productId:long}/stock")]
[Authorize]
[RequiredScope("access_as_user")]
public class StockController : ControllerBase
{
    private readonly GetStockHistory _getStockHistory;
    private readonly IGetRestockCostSuggestion _getRestockCostSuggestion;
    private readonly AdjustStock _adjustStock;

    public StockController(
        GetStockHistory getStockHistory, IGetRestockCostSuggestion getRestockCostSuggestion, AdjustStock adjustStock)
    {
        _getStockHistory = getStockHistory;
        _getRestockCostSuggestion = getRestockCostSuggestion;
        _adjustStock = adjustStock;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StockAdjustment>>> History(long productId, CancellationToken cancellationToken)
    {
        var history = await _getStockHistory.Handle(productId, cancellationToken);
        if (history.Count == 0) return NotFound("Product not found");
        return Ok(history.Select(StockAdjustmentResponseMapper.ToStockAdjustment).ToList());
    }

    [HttpGet("restock-cost-suggestion")]
    public async Task<ActionResult<RestockCostSuggestionDto>> GetRestockCostSuggestion(long productId, CancellationToken cancellationToken)
    {
        var suggestion = await _getRestockCostSuggestion.Handle(productId, cancellationToken);
        return suggestion is null
            ? NotFound()
            : Ok(new RestockCostSuggestionDto(suggestion.UnitCost, suggestion.Source, suggestion.PurchaseDate));
    }

    [HttpPost]
    public async Task<ActionResult<StockAdjustment>> Adjust(long productId, StockAdjustmentDto dto, CancellationToken cancellationToken)
    {
        // InsufficientStockException and DomainValidationException are mapped centrally by
        // DomainExceptionHandler (see Http/DomainExceptionHandler.cs) into a 400 ProblemDetails
        // with the same message this action used to return directly.
        var input = new ManualStockAdjustmentInput(
            dto.QuantityChange, (DomainStock.StockAdjustmentReason)dto.Reason, dto.Notes, dto.MachineId, dto.EatBefore, dto.UnitCost);
        var adjustment = await _adjustStock.Handle(productId, input, cancellationToken);

        if (adjustment is null) return BadRequest("Invalid product or resulting quantity");
        return Ok(StockAdjustmentResponseMapper.ToStockAdjustment(adjustment));
    }
}
