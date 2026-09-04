using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed class InventoryCostService : IInventoryCostService
{
    private readonly AppDbContext _db;

    public InventoryCostService(AppDbContext db) => _db = db;

    public StockAdjustment ApplyMovement(
        long productId,
        int quantityChange,
        StockAdjustmentReason reason,
        ReceiptItem? receiptItem,
        string notes,
        decimal? purchaseUnitCost = null)
    {
        var product = _db.Products.Local.FirstOrDefault(p => p.Id == productId) ??
            _db.Products.Find(productId);
        if (product is null)
            throw new InvalidOperationException($"Product {productId} was not loaded.");

        if (quantityChange > 0 && reason == StockAdjustmentReason.Restock)
        {
            if (purchaseUnitCost.HasValue && purchaseUnitCost.Value < 0)
                throw new InvalidOperationException($"Purchase cost cannot be negative for product {productId}.");
            if (product.QuantityInStock < 0)
                throw new InventoryCostDataQualityException(
                    $"Product {productId} has negative stock and cannot be weighted-average costed.");

            if (purchaseUnitCost.HasValue)
            {
                var oldQuantity = product.QuantityInStock;
                var newQuantity = checked(oldQuantity + quantityChange);
                product.AverageUnitCost = oldQuantity == 0
                    ? purchaseUnitCost.Value
                    : ((oldQuantity * product.AverageUnitCost) +
                       (quantityChange * purchaseUnitCost.Value)) / newQuantity;
            }
        }

        var newStockQuantity = checked(product.QuantityInStock + quantityChange);
        if (newStockQuantity < 0)
            throw new InsufficientStockException(product.QuantityInStock);

        var movementUnitCost = quantityChange < 0
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
            ReceiptItem = receiptItem,
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
