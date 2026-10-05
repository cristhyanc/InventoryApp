using Inventory.Application.Costing;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryCostTransitionStore"/> (issue #298,
/// child 4 of #149). It still lives in InventoryApi, not Inventory.Infrastructure:
/// <see cref="AppDbContext"/> and the <see cref="InventoryCostTransitionBaseline"/>,
/// <see cref="InventoryCostTransitionPreviewDraft"/>, <see cref="Product"/> and
/// <see cref="StockAdjustment"/> persistence models it depends on moved there in issue #307, and
/// moving this adapter family after them is Persistence 7/8 and 8/8 of #153.
///
/// The queries and the baseline mapping are unchanged from the former
/// <c>InventoryCostTransitionService</c>, all read and written through <see cref="AppDbContext"/>'s
/// business query filter and ownership stamp; the adapter adds no business filter of its own and
/// applies no transition rule. A single-product preview is stored with its product ID and an
/// all-products preview with product ID <c>0</c>, as before. On a relational provider an apply runs
/// in a database transaction; the EF InMemory provider used by some tests has none.
/// </summary>
public sealed class EfInventoryCostTransitionStore : IInventoryCostTransitionStore
{
    private readonly AppDbContext _db;

    public EfInventoryCostTransitionStore(AppDbContext db) => _db = db;

    public async Task<IInventoryCostTransitionTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new Transaction(_db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null);

    public Task<bool> AnyBaselineAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
        _db.InventoryCostTransitionBaselines.AnyAsync(x => productIds.Contains(x.ProductId), cancellationToken);

    public async Task<InventoryCostTransitionProduct?> GetProductAsync(long productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        return product is null ? null : ToTransitionProduct(product);
    }

    public async Task<IReadOnlyList<InventoryCostTransitionProduct>> ListProductsWithoutBaselineAsync(
        CancellationToken cancellationToken)
    {
        var baselineProductIds = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Select(x => x.ProductId)
            .ToListAsync(cancellationToken);
        return (await _db.Products.AsNoTracking()
                .Where(x => !baselineProductIds.Contains(x.Id))
                .OrderBy(x => x.Name)
                .ToListAsync(cancellationToken))
            .Select(ToTransitionProduct)
            .ToList();
    }

    public async Task<IReadOnlyList<InventoryCostTransitionProduct>> ListProductsAsync(
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken) =>
        (await _db.Products.AsNoTracking()
            .Where(x => productIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken))
        .Select(ToTransitionProduct)
        .ToList();

    public async Task<IReadOnlyDictionary<long, int>> SumPhysicalMovementsAsync(
        IReadOnlyCollection<long> productIds,
        DateTime cutoffAt,
        CancellationToken cancellationToken)
    {
        var adjustments = await _db.StockAdjustments.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId) && x.EffectiveAt <= cutoffAt)
            .Select(x => new { x.ProductId, x.QuantityChange })
            .ToListAsync(cancellationToken);
        return adjustments
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityChange));
    }

    public void AddDraft(NewInventoryCostTransitionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _db.InventoryCostTransitionPreviewDrafts.Add(new InventoryCostTransitionPreviewDraft
        {
            Id = draft.Id,
            ProductId = draft.ProductId,
            SnapshotJson = draft.SnapshotJson,
            CreatedAt = draft.CreatedAt,
            ExpiresAt = draft.ExpiresAt
        });
    }

    public async Task<StoredInventoryCostTransitionDraft?> FindDraftAsync(
        Guid previewId,
        InventoryCostTransitionPreviewScope scope,
        CancellationToken cancellationToken)
    {
        var query = _db.InventoryCostTransitionPreviewDrafts.Where(x => x.Id == previewId);
        query = scope == InventoryCostTransitionPreviewScope.AllProducts
            ? query.Where(x => x.ProductId == 0)
            : query.Where(x => x.ProductId > 0);
        var draft = await query.SingleOrDefaultAsync(cancellationToken);
        return draft is null
            ? null
            : new StoredInventoryCostTransitionDraft(draft.Id, draft.SnapshotJson, draft.ExpiresAt, draft.AppliedAt);
    }

    public void MarkDraftApplied(Guid previewId, DateTime appliedAt)
    {
        var draft = _db.InventoryCostTransitionPreviewDrafts.Local.SingleOrDefault(x => x.Id == previewId)
            ?? throw new InvalidOperationException("The transition preview was not loaded for update.");
        draft.AppliedAt = appliedAt;
    }

    public void AddBaseline(InventoryCostTransitionPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        _db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = preview.ProductId,
            CutoffAt = preview.CutoffAt,
            HomeStockQuantity = preview.HomeStockQuantity,
            MachineStockQuantity = preview.MachineStockQuantity,
            OpeningCostingQuantity = preview.OpeningCostingQuantity,
            AverageUnitCost = preview.AverageUnitCost,
            InventoryValue = preview.InventoryValue,
            CostSource = (InventoryCostBaselineSource)preview.CostSource,
            LegacyReplayedPhysicalQuantity = preview.LegacyReplayedPhysicalQuantity,
            LegacyPhysicalDiscrepancy = preview.LegacyPhysicalDiscrepancy,
            DataQualityNote = preview.DataQualityNote,
            MachineStocks = preview.MachineStocks.Select(x => new InventoryCostTransitionMachineStock
            {
                MachineId = x.MachineId,
                MachineName = x.MachineName,
                StockQuantity = x.StockQuantity,
                Source = x.Source
            }).ToList()
        });
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private static InventoryCostTransitionProduct ToTransitionProduct(Product product) =>
        new(product.Id, product.Name, product.QuantityInStock, product.AverageUnitCost);

    private sealed class Transaction(IDbContextTransaction? transaction) : IInventoryCostTransitionTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
