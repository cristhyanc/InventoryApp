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
/// The global Stock History read endpoint (issue #384): one bounded, tenant-scoped, filterable query
/// over the movement history of every product the caller's business owns, so the page behind it does
/// not have to ask for one product's history at a time.
///
/// It is a read-only addition. Stock movements are still created only through
/// <c>POST /api/products/{productId}/stock</c> (<see cref="StockController"/>), which remains the
/// single authority on costing, insufficient-stock validation and reason/source semantics; this
/// controller has no write action and no costing rule.
///
/// It requires the same <c>Inventory.Operate</c> capability as the per-product write endpoint
/// (issue #525): the history it reports is the same movement data, read in one page instead of one
/// product at a time.
/// </summary>
[ApiController]
[Route("api/stock-history")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public sealed class StockHistoryController : ControllerBase
{
    private readonly ListStockHistory _listStockHistory;

    public StockHistoryController(ListStockHistory listStockHistory) => _listStockHistory = listStockHistory;

    /// <summary>
    /// One page of stock movements, most recent first.
    ///
    /// <para><c>from</c>/<c>to</c> are the inclusive first and last business
    /// days to cover: the client sends calendar dates, and the server converts them to the UTC
    /// boundaries of those business days, so the last day is covered whole however long it is.</para>
    ///
    /// <para><c>pageSize</c> is a request, not an instruction - the server bounds it (see
    /// <see cref="Inventory.Application.Stock.StockHistoryPaging"/>) and reports the page it
    /// actually served.</para>
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<StockHistoryPageResponse>> Get(
        [FromQuery] StockHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = new StockHistoryQuery(
            request.ProductId,
            request.From,
            request.To,
            (DomainStock.StockAdjustmentReason?)request.Reason,
            request.MachineId,
            (DomainStock.StockAdjustmentSource?)request.Source,
            request.Page,
            request.PageSize);

        var result = await _listStockHistory.Handle(query, cancellationToken);
        return Ok(StockHistoryResponseMapper.ToResponse(result));
    }
}
