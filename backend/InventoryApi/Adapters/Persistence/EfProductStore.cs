using Inventory.Application.Products;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IProductStore"/>. It lives in InventoryApi, not
/// Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and persistence models
/// that still live in InventoryApi, following the same precedent as <c>EfCategoryStore</c>/
/// <c>EfSupplierStore</c>/<c>EfOperatingExpenseStore</c>. Move it into Inventory.Infrastructure once
/// <see cref="AppDbContext"/> and the shared persistence models relocate there.
/// </summary>
public sealed class EfProductStore : IProductStore
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostRebuildService _rebuild;

    public EfProductStore(AppDbContext db, IInventoryCostRebuildService rebuild)
    {
        _db = db;
        _rebuild = rebuild;
    }

    public async Task<long> CreateAsync(ProductCreateFields fields, CancellationToken cancellationToken)
    {
        var product = new Product
        {
            Name = fields.Name,
            Sku = fields.Sku,
            Description = fields.Description,
            UnitPrice = fields.UnitPrice,
            AverageUnitCost = fields.InitialUnitCost ?? 0m,
            IsActive = fields.IsActive,
            QuantityInStock = fields.QuantityInStock,
            LowStockThreshold = fields.LowStockThreshold,
            RestockTo = fields.RestockTo,
            Unit = fields.Unit,
            CategoryId = fields.CategoryId,
            SupplierId = fields.SupplierId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync(cancellationToken);

        if (product.QuantityInStock > 0)
        {
            _db.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = product.Id,
                QuantityChange = product.QuantityInStock,
                QuantityAfter = product.QuantityInStock,
                Reason = StockAdjustmentReason.Restock,
                UnitCost = fields.InitialUnitCost,
                TotalCost = fields.InitialUnitCost * product.QuantityInStock,
                Notes = "Initial stock on product creation",
                EffectiveAt = product.CreatedAt,
            });
            await _db.SaveChangesAsync(cancellationToken);

            if (fields.InitialUnitCost.HasValue)
            {
                await _rebuild.RebuildAsync(product.Id, cancellationToken: cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return product.Id;
    }

    public Task<bool> ExistsAsync(long id, CancellationToken cancellationToken) =>
        _db.Products.AnyAsync(product => product.Id == id, cancellationToken);

    public async Task<bool> UpdateAsync(long id, ProductUpdateFields fields, CancellationToken cancellationToken)
    {
        var product = await _db.Products.FindAsync([id], cancellationToken);
        if (product is null) return false;

        product.Sku = fields.Sku;
        product.Description = fields.Description;
        product.IsActive = fields.IsActive;
        product.LowStockThreshold = fields.LowStockThreshold;
        product.RestockTo = fields.RestockTo;
        product.Unit = fields.Unit;
        product.SupplierId = fields.SupplierId;
        product.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var product = await _db.Products.FindAsync([id], cancellationToken);
        if (product is null) return false;

        _db.Products.Remove(product);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
