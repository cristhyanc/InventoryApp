using Inventory.Application.Costing;
using Inventory.Application.Stock;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IStockAdjustmentStore"/> (issue #282). It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and
/// the persistence models, following the same precedent as <c>EfPurchaseStore</c>/
/// <c>EfProductStore</c>; move it into Inventory.Infrastructure once <see cref="AppDbContext"/> and
/// the shared persistence models relocate there (issue #153).
///
/// <see cref="ApplyAsync"/> records the movement through the Application
/// <see cref="IRecordInventoryMovement"/> use case and rebuilds the product's cost through
/// <see cref="IRebuildProductCost"/> (issue #296), inside the same transaction as the former
/// <c>InventoryApi.Services.StockService.Adjust</c>, stamping <see cref="StockAdjustmentSource.Manual"/>,
/// the machine id, and the eat-before date on the recorded movement. It applies no costing rule of
/// its own.
/// </summary>
public sealed class EfStockAdjustmentStore : IStockAdjustmentStore
{
    private readonly AppDbContext _db;
    private readonly IRecordInventoryMovement _recordMovement;
    private readonly IRebuildProductCost _rebuild;

    public EfStockAdjustmentStore(AppDbContext db, IRecordInventoryMovement recordMovement, IRebuildProductCost rebuild)
    {
        _db = db;
        _recordMovement = recordMovement;
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
        var movement = await _recordMovement.RecordAsync(
            new InventoryMovement(
                productId, input.QuantityChange, input.Reason, input.Notes ?? string.Empty, input.UnitCost,
                input.MachineId, input.EatBefore, DomainStock.StockAdjustmentSource.Manual),
            cancellationToken);

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

        var adjustment = await _db.StockAdjustments.FindAsync([movement.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Stock adjustment {movement.Id} was not saved.");
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
