using Inventory.Application.Costing;
using Inventory.Application.Products;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductStore"/>. It lives in Inventory.Infrastructure,
/// following the same precedent as <see cref="EfCategoryStore"/>/
/// <see cref="EfSupplierStore"/>/<see cref="EfOperatingExpenseStore"/>: <see cref="AppDbContext"/>
/// and the persistence models it depends on moved there in issue #307, and this adapter family
/// followed them in issue #309 (Persistence 8/8 of #153).
/// </summary>
public sealed class EfProductStore : IProductStore
{
    private readonly AppDbContext _db;
    private readonly IRebuildProductCost _rebuild;

    public EfProductStore(AppDbContext db, IRebuildProductCost rebuild)
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

    public async Task<GstClassification?> FindGstRuleAsync(long id, CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Where(product => product.Id == id)
            .Select(product => new { product.GstRule })
            .SingleOrDefaultAsync(cancellationToken);

        return product?.GstRule;
    }

    /// <summary>
    /// Writes the rule column only. <c>UpdatedAt</c> is deliberately left alone as well, because it
    /// describes the catalogue record the Nayax import and the product edit maintain; a GST rule is
    /// separate bookkeeping configuration and must not look like a catalogue change.
    /// </summary>
    public async Task<bool> SetGstRuleAsync(long id, GstClassification rule, CancellationToken cancellationToken)
    {
        var product = await _db.Products.FindAsync([id], cancellationToken);
        if (product is null) return false;

        product.GstRule = rule;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
