using Inventory.Application.Access;
using Inventory.Application.Stock;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Controllers;

/// <summary>
/// One product's stock movements: its history, the restock cost suggestion and the single write
/// action that creates a movement. Recording a movement is day-to-day inventory work, so the
/// controller requires <c>Inventory.Operate</c> (issue #525).
///
/// <para><b>A movement's <c>inventoryValueAfter</c> is withheld from a caller without
/// <c>Dashboard.ViewFinancials</c></b> (issue #700). It is the business's inventory value at cost
/// immediately after the movement, so the latest movement reports the current valuation - the
/// figure the Dashboard withholds (issue #527), <c>inventory-value-summary</c> refuses (issue #681)
/// and the catalogue no longer carries (issue #698). Both actions stay available to every role that
/// may operate inventory, and the movement's own unit cost, total cost, resulting average unit cost
/// and every quantity are unchanged; only that one figure differs, decided once per request from
/// <see cref="CurrentCapabilities"/> and never from a role name.</para>
/// </summary>
[ApiController]
[Route("api/products/{productId:long}/stock")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public class StockController : ControllerBase
{
    private readonly GetStockHistory _getStockHistory;
    private readonly IGetRestockCostSuggestion _getRestockCostSuggestion;
    private readonly AdjustStock _adjustStock;
    private readonly CurrentCapabilities _capabilities;

    public StockController(
        GetStockHistory getStockHistory,
        IGetRestockCostSuggestion getRestockCostSuggestion,
        AdjustStock adjustStock,
        CurrentCapabilities capabilities)
    {
        _getStockHistory = getStockHistory;
        _getRestockCostSuggestion = getRestockCostSuggestion;
        _adjustStock = adjustStock;
        _capabilities = capabilities;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductStockAdjustmentResponse>>> History(
        long productId, CancellationToken cancellationToken)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(cancellationToken);
        var history = await _getStockHistory.Handle(productId, cancellationToken);
        if (history.Count == 0) return NotFound("Product not found");
        return Ok(history
            .Select(movement => StockAdjustmentResponseMapper.ToResponse(movement, visibility))
            .ToList());
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
    public async Task<ActionResult<ProductStockAdjustmentResponse>> Adjust(
        long productId, StockAdjustmentDto dto, CancellationToken cancellationToken)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(cancellationToken);

        // InsufficientStockException and DomainValidationException are mapped centrally by
        // DomainExceptionHandler (see Http/DomainExceptionHandler.cs) into a 400 ProblemDetails
        // with the same message this action used to return directly.
        var input = new ManualStockAdjustmentInput(
            dto.QuantityChange, (DomainStock.StockAdjustmentReason)dto.Reason, dto.Notes, dto.MachineId, dto.EatBefore, dto.UnitCost);
        var adjustment = await _adjustStock.Handle(productId, input, cancellationToken);

        if (adjustment is null) return BadRequest("Invalid product or resulting quantity");
        return Ok(StockAdjustmentResponseMapper.ToResponse(adjustment, visibility));
    }
}
