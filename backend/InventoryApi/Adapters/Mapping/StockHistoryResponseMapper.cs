using Inventory.Application.Stock;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's bounded <see cref="StockHistoryPage"/> onto the API-owned
/// <see cref="StockHistoryPageResponse"/> the global Stock History endpoint serializes (issue #384).
///
/// It is transport mapping only: it carries the persisted movement fields across, keeps the
/// resolved page the use case decided, and derives nothing. <c>HasMore</c> comes from the page
/// itself rather than being recomputed here, so the client and the use case cannot disagree about
/// whether more movements remain.
///
/// <para>The one exception since issue #700 is <see cref="FinancialFigureVisibility"/>:
/// <see cref="StockHistoryEntryResponse.InventoryValueAfter"/> is the same withheld figure
/// <see cref="StockAdjustmentResponseMapper"/> withholds on the per-product endpoints, and reading
/// one page of every product's movements must not be a way around that. The movement's own unit
/// cost, total cost and resulting average unit cost are unchanged for every role.</para>
/// </summary>
public static class StockHistoryResponseMapper
{
    public static StockHistoryPageResponse ToResponse(
        StockHistoryPage page, FinancialFigureVisibility visibility) => new(
            page.Entries.Select(entry => ToResponse(entry, visibility)).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount,
            page.HasMore);

    public static StockHistoryEntryResponse ToResponse(
        StockHistoryEntry entry, FinancialFigureVisibility visibility) => new(
            entry.Movement.Id,
            entry.Movement.ProductId,
            entry.ProductName,
            entry.Movement.ReceiptItemId,
            entry.Movement.QuantityChange,
            entry.Movement.QuantityAfter,
            entry.Movement.UnitCost,
            entry.Movement.TotalCost,
            entry.Movement.CostingQuantityAfter,
            entry.Movement.AverageUnitCostAfter,
            visibility.Withhold(entry.Movement.InventoryValueAfter),
            (StockAdjustmentReason)entry.Movement.Reason,
            (StockAdjustmentSource)entry.Movement.Source,
            entry.Movement.MachineId,
            entry.Movement.Notes,
            entry.Movement.EatBefore,
            entry.Movement.CreatedAt);
}
