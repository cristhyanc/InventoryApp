using Inventory.Application.Stock;
using InventoryApi.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Maps the Application layer's <see cref="StockAdjustmentRecord"/> back onto the
/// <see cref="StockAdjustment"/> shape the stock endpoints have always serialised (issue #282):
/// every key this API response already carries is reproduced here. The returned instance is a
/// detached response object, never attached to a <see cref="Data.AppDbContext"/>.
/// </summary>
internal static class StockAdjustmentResponseMapper
{
    public static StockAdjustment ToStockAdjustment(StockAdjustmentRecord record) => new()
    {
        BusinessId = record.BusinessId,
        Id = record.Id,
        ProductId = record.ProductId,
        ReceiptItemId = record.ReceiptItemId,
        QuantityChange = record.QuantityChange,
        QuantityAfter = record.QuantityAfter,
        UnitCost = record.UnitCost,
        TotalCost = record.TotalCost,
        CostingQuantityAfter = record.CostingQuantityAfter,
        AverageUnitCostAfter = record.AverageUnitCostAfter,
        InventoryValueAfter = record.InventoryValueAfter,
        Reason = (StockAdjustmentReason)record.Reason,
        Source = (StockAdjustmentSource)record.Source,
        MachineId = record.MachineId,
        Notes = record.Notes,
        EatBefore = record.EatBefore,
        CreatedAt = record.CreatedAt,
        EffectiveAt = record.EffectiveAt,
    };
}
