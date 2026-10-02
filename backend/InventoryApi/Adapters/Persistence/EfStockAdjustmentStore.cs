using Inventory.Application.Stock;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IStockAdjustmentStore"/> (issue #282). It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and
/// the existing costing services, following the same precedent as <c>EfPurchaseStore</c>/
/// <c>EfProductStore</c>; move it into Inventory.Infrastructure once <see cref="AppDbContext"/> and
/// the shared persistence models relocate there (issue #153).
///
/// <see cref="ApplyAsync"/> reuses <see cref="IInventoryCostService.ApplyMovement"/> and
/// <see cref="IInventoryCostRebuildService.RebuildAsync"/> exactly as the former
/// <c>InventoryApi.Services.StockService.Adjust</c> did - the costing algorithm itself stays out of
/// scope for this slice (issue #149) - and stamps <see cref="StockAdjustmentSource.Manual"/>, the
/// machine id, and the eat-before date the same way the former service did, after the movement is
/// created.
/// </summary>
public sealed class EfStockAdjustmentStore : IStockAdjustmentStore
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostService _costing;
    private readonly IInventoryCostRebuildService _rebuild;

    public EfStockAdjustmentStore(AppDbContext db, IInventoryCostService costing, IInventoryCostRebuildService rebuild)
    {
        _db = db;
        _costing = costing;
        _rebuild = rebuild;
    }

    public Task<bool> ProductExistsAsync(long productId, CancellationToken cancellationToken) =>
        _db.Products.AnyAsync(p => p.Id == productId, cancellationToken);

    public async Task<IReadOnlyList<StockAdjustmentRecord>> ListHistoryAsync(long productId, CancellationToken cancellationToken)
    {
        var adjustments = await _db.StockAdjustments
            .Where(sa => sa.ProductId == productId)
            .OrderByDescending(sa => sa.CreatedAt)
            .ToListAsync(cancellationToken);
        return adjustments.Select(ToRecord).ToList();
    }

    public async Task<RestockCostFacts?> GetRestockCostFactsAsync(long productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null) return null;

        var lastPurchase = await _db.ReceiptItems
            .AsNoTracking()
            .Where(item => item.ProductId == productId)
            .OrderByDescending(item => item.Purchase!.PurchaseDate)
            .ThenByDescending(item => item.ReceiptId)
            .ThenByDescending(item => item.Id)
            .Select(item => new { item.UnitCost, item.Purchase!.PurchaseDate })
            .FirstOrDefaultAsync(cancellationToken);

        var lastPurchaseFact = lastPurchase is null
            ? null
            : new DomainStock.LastPurchaseCostFact(lastPurchase.UnitCost, lastPurchase.PurchaseDate);

        return new RestockCostFacts(lastPurchaseFact, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost);
    }

    public async Task<StockAdjustmentRecord> ApplyAsync(
        long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken)
    {
        var adjustment = _costing.ApplyMovement(
            productId, input.QuantityChange, (StockAdjustmentReason)input.Reason, null,
            input.Notes ?? string.Empty, input.UnitCost);
        adjustment.MachineId = input.MachineId;
        adjustment.EatBefore = input.EatBefore;

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return ToRecord(adjustment);
    }

    private static StockAdjustmentRecord ToRecord(StockAdjustment adjustment) => new(
        adjustment.Id,
        adjustment.BusinessId,
        adjustment.ProductId,
        adjustment.ReceiptItemId,
        adjustment.QuantityChange,
        adjustment.QuantityAfter,
        adjustment.UnitCost,
        adjustment.TotalCost,
        adjustment.CostingQuantityAfter,
        adjustment.AverageUnitCostAfter,
        adjustment.InventoryValueAfter,
        (DomainStock.StockAdjustmentReason)adjustment.Reason,
        (DomainStock.StockAdjustmentSource)adjustment.Source,
        adjustment.MachineId,
        adjustment.Notes,
        adjustment.EatBefore,
        adjustment.CreatedAt,
        adjustment.EffectiveAt);
}
