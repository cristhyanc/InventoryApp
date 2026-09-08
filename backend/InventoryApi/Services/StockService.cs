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

    public async Task<StockAdjustment?> Adjust(long productId, StockAdjustmentDto dto)
    {
        var productExists = await _db.Products.AnyAsync(p => p.Id == productId);
        if (!productExists) return null;

        var adjustment = _costing.ApplyMovement(productId, dto.QuantityChange, dto.Reason, null,
            dto.Notes ?? string.Empty);
        adjustment.MachineId = dto.MachineId;
        adjustment.EatBefore = dto.EatBefore;
        await _db.SaveChangesAsync();
        await _rebuild.RebuildAsync(productId);
        await _db.SaveChangesAsync();

        return adjustment;
    }
}
