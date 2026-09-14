using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class StockService : IStockService
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostService _costing;
    private readonly IInventoryCostRebuildService _rebuild;
    public StockService(
        AppDbContext db,
        IInventoryCostService? costing = null,
        IInventoryCostRebuildService? rebuild = null)
    {
        _db = db;
        _costing = costing ?? new InventoryCostService(db);
        _rebuild = rebuild ?? new InventoryCostRebuildService(db);
    }

    public async Task<IEnumerable<StockAdjustment>> History(long productId)
    {
        var exists = await _db.Products.AnyAsync(p => p.Id == productId);
        if (!exists) return Enumerable.Empty<StockAdjustment>();

        return await _db.StockAdjustments
            .Where(sa => sa.ProductId == productId)
            .OrderByDescending(sa => sa.CreatedAt)
            .ToListAsync();
    }

    public async Task<RestockCostSuggestionDto?> GetRestockCostSuggestion(long productId)
    {
        var product = await _db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == productId);
        if (product is null) return null;

        var lastPurchase = await _db.ReceiptItems
            .AsNoTracking()
            .Where(item => item.ProductId == productId)
            .OrderByDescending(item => item.Receipt!.PurchaseDate)
            .ThenByDescending(item => item.ReceiptId)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.UnitCost, item.Receipt!.PurchaseDate })
            .FirstOrDefaultAsync();
        if (lastPurchase is not null)
            return new RestockCostSuggestionDto(lastPurchase.UnitCost, "LastPurchase", lastPurchase.PurchaseDate);

        if (product.CostingQuantity is > 0 && product.InventoryValue.HasValue && product.AverageUnitCost >= 0)
            return new RestockCostSuggestionDto(product.AverageUnitCost, "AverageUnitCost", null);

        return new RestockCostSuggestionDto(null, "None", null);
    }

    public async Task<StockAdjustment?> Adjust(long productId, StockAdjustmentDto dto)
    {
        var productExists = await _db.Products.AnyAsync(p => p.Id == productId);
        if (!productExists) return null;

        if (dto.Reason == StockAdjustmentReason.Restock && dto.QuantityChange > 0)
        {
            if (!dto.UnitCost.HasValue)
                throw new ArgumentException("Unit cost is required for a positive Restock adjustment.");
            if (dto.UnitCost.Value < 0)
                throw new ArgumentException("Unit cost cannot be negative for a positive Restock adjustment.");
        }

        var adjustment = _costing.ApplyMovement(productId, dto.QuantityChange, dto.Reason, null,
            dto.Notes ?? string.Empty, dto.UnitCost);
        adjustment.MachineId = dto.MachineId;
        adjustment.EatBefore = dto.EatBefore;

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await _db.SaveChangesAsync();
            await _rebuild.RebuildAsync(productId);
            await _db.SaveChangesAsync();
            if (transaction is not null)
                await transaction.CommitAsync();
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync();
            throw;
        }

        return adjustment;
    }
}
