using Inventory.Domain.Exceptions;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

public sealed class InventoryCostService : IInventoryCostService
{
    private readonly AppDbContext _db;

    public InventoryCostService(AppDbContext db) => _db = db;

    public StockAdjustment ApplyMovement(
        long productId,
        int quantityChange,
        StockAdjustmentReason reason,
        PurchaseItem? purchaseItem,
        string notes,
        decimal? purchaseUnitCost = null)
    {
        var product = _db.Products.Local.FirstOrDefault(p => p.Id == productId) ??
            _db.Products.Find(productId);
        if (product is null)
            throw new InvalidOperationException($"Product {productId} was not loaded.");

        if (purchaseUnitCost is < 0)
            throw new InvalidOperationException($"Purchase cost cannot be negative for product {productId}.");

        var newStockQuantity = checked(product.QuantityInStock + quantityChange);
        if (newStockQuantity < 0)
            throw new InsufficientStockException(product.QuantityInStock);

        var movementUnitCost = quantityChange < 0 && product.CostingQuantity is > 0 && product.AverageUnitCost >= 0
            ? product.AverageUnitCost
            : reason == StockAdjustmentReason.Restock
                ? purchaseUnitCost
                : null;

        product.QuantityInStock = newStockQuantity;
        product.UpdatedAt = DateTime.UtcNow;

        var adjustment = new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = quantityChange,
            QuantityAfter = newStockQuantity,
            Reason = reason,
            ReceiptItem = purchaseItem,
            UnitCost = movementUnitCost,
            TotalCost = movementUnitCost.HasValue
                ? movementUnitCost.Value * Math.Abs(quantityChange)
                : null,
            Notes = notes
        };
        _db.StockAdjustments.Add(adjustment);
        return adjustment;
    }
}

public sealed class InventoryCostDataQualityException : InvalidOperationException
{
    public InventoryCostDataQualityException(string message) : base(message) { }
}
