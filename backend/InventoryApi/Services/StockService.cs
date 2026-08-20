using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class StockService : IStockService
{
    private readonly AppDbContext _db;
    public StockService(AppDbContext db) => _db = db;

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
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return null;

        var newQuantity = product.QuantityInStock + dto.QuantityChange;
        if (newQuantity < 0) throw new InsufficientStockException(product.QuantityInStock);

        product.QuantityInStock = newQuantity;
        product.UpdatedAt = DateTime.UtcNow;

        var adjustment = new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = dto.QuantityChange,
            QuantityAfter = newQuantity,
            Reason = dto.Reason,
            MachineId = dto.MachineId,
            Notes = dto.Notes,
            EatBefore = dto.EatBefore
        };
        _db.StockAdjustments.Add(adjustment);
        await _db.SaveChangesAsync();

        return adjustment;
    }
}
