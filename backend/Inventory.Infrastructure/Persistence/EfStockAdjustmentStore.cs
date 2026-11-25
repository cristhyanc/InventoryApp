using Inventory.Application.Costing;
using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IStockAdjustmentStore"/> (issue #282). It lives in
/// Inventory.Infrastructure, following the same precedent as
/// <see cref="EfPurchaseStore"/>/<see cref="EfProductStore"/>: <see cref="AppDbContext"/> and the
/// persistence models it depends on moved there in issue #307, and this adapter family followed them
/// in issue #309 (Persistence 8/8 of #153).
///
/// <see cref="ApplyAsync"/> records the movement through the Application
/// <see cref="IRecordInventoryMovement"/> use case and rebuilds the product's cost through
/// <see cref="IRebuildProductCost"/> (issue #296), inside one transaction as the former
/// <c>InventoryApi.Services.StockService.Adjust</c> did, stamping <see cref="StockAdjustmentSource.Manual"/>,
/// the machine id, and the eat-before date on the recorded movement. It applies no costing rule of
/// its own. Since issue #1241 that transaction is <see cref="SqliteWriteLockTransaction"/> and it is
/// opened before the movement is staged, so the stock the sufficiency check reads and the stock the
/// movement is applied to are the same one.
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

    public async Task<ManualStockAdjustmentProductFacts?> FindProductFactsAsync(
        long productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Name, p.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        return product is null ? null : new ManualStockAdjustmentProductFacts(product.Name, product.IsActive);
    }

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

    /// <summary>
    /// Records the movement and rebuilds the product's cost in one write-locked transaction (issue
    /// #1241). The <c>BEGIN IMMEDIATE</c> lock is taken before <see cref="IRecordInventoryMovement"/>
    /// reads the stored quantity, because that read is what the sufficiency check is decided on:
    /// staged outside it, two simultaneous write-offs of 4 against a stock of 5 both read 5, both
    /// computed a new stock of 1, and the loser's movement was refused by the cost replay as
    /// physically negative - a costing data-quality failure for an adjustment that simply no longer
    /// fitted. Inside it the loser reads the quantity the winner left and is refused with the
    /// established <see cref="Inventory.Domain.Exceptions.InsufficientStockException"/>.
    ///
    /// <para>Since issue #1249 the product's retirement is re-read inside that same lock, for the
    /// same reason: <see cref="Inventory.Application.Stock.AdjustStock"/>'s own check happens before
    /// this transaction opens, so a retirement committed in between would otherwise be followed by
    /// exactly the movement, quantity change and cost rebuild a retired product may not have.</para>
    /// </summary>
    public async Task<StockAdjustmentRecord> ApplyAsync(
        long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        await EnsureProductIsNotRetiredAsync(productId, cancellationToken);

        var movement = await _recordMovement.RecordAsync(
            new InventoryMovement(
                productId, input.QuantityChange, input.Reason, input.Notes ?? string.Empty, input.UnitCost,
                input.MachineId, input.EatBefore, DomainStock.StockAdjustmentSource.Manual),
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var adjustment = await _db.StockAdjustments.FindAsync([movement.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Stock adjustment {movement.Id} was not saved.");
        return ToRecord(adjustment);
    }

    /// <summary>
    /// The authoritative retired-product refusal for a manual adjustment (issue #1249), read inside
    /// the caller's write-locked transaction and before anything is staged, so the product state it
    /// refuses on is the state the movement would be committed against.
    ///
    /// <para>It reads through <see cref="FindProductFactsAsync"/>, so the read is tenant-scoped by
    /// the <see cref="AppDbContext"/> query filters exactly as the use case's own is, and it applies
    /// the adjustment's Domain rule
    /// (<see cref="DomainStock.ManualStockAdjustmentPolicy.RetiredProductMessage"/>) rather than a
    /// sentence of its own. It sits here rather than in the shared
    /// <see cref="IRecordInventoryMovement"/> deliberately: Sync Restock records its movement through
    /// that use case and must stay allowed for a retired product, because the machine really was
    /// refilled.</para>
    ///
    /// <para>A product that no longer exists at all is left exactly as it was before this issue -
    /// unnamed here, and refused by the foreign key - because inventing a not-found answer inside the
    /// write would change a contract this issue does not touch.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">The product is retired.</exception>
    private async Task EnsureProductIsNotRetiredAsync(long productId, CancellationToken cancellationToken)
    {
        var product = await FindProductFactsAsync(productId, cancellationToken);
        if (product is not null && !product.IsActive)
        {
            throw new DomainValidationException(
                DomainStock.ManualStockAdjustmentPolicy.RetiredProductMessage(product.Name));
        }
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
