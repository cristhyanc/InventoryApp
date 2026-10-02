using Inventory.Domain.Costing;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using DomainStock = Inventory.Domain.Stock;

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

        var movementCost = StockMovementCostPolicy.Calculate(
            product.QuantityInStock,
            quantityChange,
            (DomainStock.StockAdjustmentReason)reason,
            product.CostingQuantity,
            product.AverageUnitCost,
            purchaseUnitCost);

        product.QuantityInStock = movementCost.NewStockQuantity;
        product.UpdatedAt = DateTime.UtcNow;

        var adjustment = new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = quantityChange,
            QuantityAfter = movementCost.NewStockQuantity,
            Reason = reason,
            ReceiptItem = purchaseItem,
            UnitCost = movementCost.UnitCost,
            TotalCost = movementCost.TotalCost,
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
