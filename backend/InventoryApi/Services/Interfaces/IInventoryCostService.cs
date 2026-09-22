using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface IInventoryCostService
{
    StockAdjustment ApplyMovement(
        long productId,
        int quantityChange,
        StockAdjustmentReason reason,
        PurchaseItem? receiptItem,
        string notes,
        decimal? purchaseUnitCost = null);
}
