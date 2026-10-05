using Inventory.Application.Stock;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
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
/// </summary>
[ApiController]
[Route("api/stock-history")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class StockHistoryController : ControllerBase
{
    private readonly ListStockHistory _listStockHistory;

    public StockHistoryController(ListStockHistory listStockHistory) => _listStockHistory = listStockHistory;

    /// <summary>
    /// One page of stock movements, most recent first.
    ///
    /// <para><c>from</c>/<c>to</c> are the inclusive first and last <c>Australia/Sydney</c> business
    /// days to cover: the client sends calendar dates, and the server converts them to the UTC
    /// boundaries of those business days, so the last day is covered whole however long it is.</para>
    ///
    /// <para><c>pageSize</c> is a request, not an instruction - the server bounds it (see
    /// <see cref="Inventory.Application.Stock.StockHistoryPaging"/>) and reports the page it
    /// actually served.</para>
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<StockHistoryPageResponse>> Get(
        [FromQuery] long? productId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] StockAdjustmentReason? reason = null,
        [FromQuery] long? machineId = null,
        [FromQuery] StockAdjustmentSource? source = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StockHistoryQuery(
            productId,
            from,
            to,
            (DomainStock.StockAdjustmentReason?)reason,
            machineId,
            (DomainStock.StockAdjustmentSource?)source,
            page,
            pageSize);

        var result = await _listStockHistory.Handle(query, cancellationToken);
        return Ok(StockHistoryResponseMapper.ToResponse(result));
    }
}
