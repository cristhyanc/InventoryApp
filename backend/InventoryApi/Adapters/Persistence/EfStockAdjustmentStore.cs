using Inventory.Application.Costing;
using Inventory.Application.Stock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IStockAdjustmentStore"/> (issue #282). It still
/// lives in InventoryApi, not Inventory.Infrastructure, following the same precedent as
/// <c>EfPurchaseStore</c>/<c>EfProductStore</c>: <see cref="AppDbContext"/> and the persistence
/// models it depends on moved there in issue #307, and moving this adapter family after them is
/// Persistence 7/8 and 8/8 of #153.
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

    /// <summary>
    /// The global stock-history query (issue #384). Every predicate comes from the already-resolved
    /// <see cref="StockHistoryFilter"/>; the only thing decided here is the SQL, and deliberately
    /// not the tenant boundary - the <see cref="AppDbContext"/> global query filter scopes both the
    /// count and the page, so there is no per-call <c>BusinessId</c> clause to go stale.
    ///
    /// <c>CreatedAt</c> descending is the same ordering instant the product-specific
    /// <see cref="ListHistoryAsync"/> uses, with the movement id as a tie-break so that paging over
    /// movements recorded in the same instant is a stable partition rather than an arbitrary one.
    /// The product name is read through the owning product's navigation in the same query, instead
    /// of leaving the caller to fan out a lookup per row.
    /// </summary>
    public async Task<StockHistoryResult> QueryHistoryAsync(StockHistoryFilter filter, CancellationToken cancellationToken)
    {
        var filtered = _db.StockAdjustments.AsNoTracking();

        if (filter.ProductId is { } productId)
            filtered = filtered.Where(sa => sa.ProductId == productId);
        if (filter.CreatedFromUtc is { } createdFrom)
            filtered = filtered.Where(sa => sa.CreatedAt >= createdFrom);
        if (filter.CreatedBeforeUtc is { } createdBefore)
            filtered = filtered.Where(sa => sa.CreatedAt < createdBefore);
        if (filter.Reason is { } reason)
            filtered = filtered.Where(sa => sa.Reason == (StockAdjustmentReason)reason);
        if (filter.Source is { } source)
            filtered = filtered.Where(sa => sa.Source == (StockAdjustmentSource)source);
        if (filter.MachineId is { } machineId)
            filtered = filtered.Where(sa => sa.MachineId == machineId);

        var totalCount = await filtered.CountAsync(cancellationToken);

        var rows = await filtered
            .OrderByDescending(sa => sa.CreatedAt)
            .ThenByDescending(sa => sa.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .Select(sa => new { Adjustment = sa, ProductName = sa.Product!.Name })
            .ToListAsync(cancellationToken);

        var entries = rows
            .Select(row => new StockHistoryEntry(ToRecord(row.Adjustment), row.ProductName))
            .ToList();
        return new StockHistoryResult(entries, totalCount);
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
