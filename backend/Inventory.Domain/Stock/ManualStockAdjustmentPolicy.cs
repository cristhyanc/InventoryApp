using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Stock;

/// <summary>
/// The deterministic validation an operator-entered manual stock adjustment must pass before any
/// movement is applied (issue #282), mirroring the former
/// <c>InventoryApi.Services.StockService.Adjust</c> inline checks and their exact messages,
/// unchanged: a Correction must remove stock, and a positive Restock must carry a non-negative unit
/// cost. Validation never reads or writes persisted state; the caller has already confirmed the
/// product exists.
/// </summary>
public static class ManualStockAdjustmentPolicy
{
    public static void Validate(StockAdjustmentReason reason, int quantityChange, decimal? unitCost)
    {
        if (reason == StockAdjustmentReason.Correction && quantityChange >= 0)
            throw new DomainValidationException("Correction quantity must remove stock.");

        if (reason == StockAdjustmentReason.Restock && quantityChange > 0)
        {
            if (!unitCost.HasValue)
                throw new DomainValidationException("Unit cost is required for a positive Restock adjustment.");
            if (unitCost.Value < 0)
                throw new DomainValidationException("Unit cost cannot be negative for a positive Restock adjustment.");
        }
    }
}
