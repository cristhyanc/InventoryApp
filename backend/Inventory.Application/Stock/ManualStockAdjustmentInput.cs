using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>An operator-entered manual stock adjustment request, as the Application layer sees it.</summary>
public sealed record ManualStockAdjustmentInput(
    int QuantityChange, StockAdjustmentReason Reason, string? Notes, long? MachineId, DateTime? EatBefore, decimal? UnitCost);
