using Inventory.Application.Costing;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryCostRepairStore"/> (issue #359). It
/// lives in InventoryApi, not Inventory.Infrastructure, because it depends on
/// <see cref="AppDbContext"/> and the <see cref="InventoryCostRepair"/> and <see cref="Product"/>
/// persistence models, which still live in InventoryApi; move it into Inventory.Infrastructure once
/// they relocate there (issue #153).
///
/// Everything it reads and writes goes through <see cref="AppDbContext"/>'s business query filter
/// and its ownership stamp on save, so the adapter adds no business filter of its own and cannot
/// write a repair for another business's product. It applies no repair rule: validation, placement
/// and the stale-preview check all belong to the Domain policy and the use cases. There is no
/// update or delete method, because a repair record has no update or delete path. On a relational
/// provider an apply runs in a database transaction; the EF InMemory provider used by some tests
/// has none.
/// </summary>
public sealed class EfInventoryCostRepairStore : IInventoryCostRepairStore
{
    private readonly AppDbContext _db;

    public EfInventoryCostRepairStore(AppDbContext db) => _db = db;

    public async Task<IInventoryCostRepairTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new Transaction(_db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null);

    public async Task<InventoryCostRepairProduct?> GetProductAsync(long productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        return product is null ? null : new InventoryCostRepairProduct(product.Id, product.Name);
    }

    public async Task<InventoryCostRepairRecord> AppendAsync(
        NewInventoryCostRepair repair,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repair);

        var row = new InventoryCostRepair
        {
            ProductId = repair.ProductId,
            EffectiveAt = repair.EffectiveAt,
            Quantity = repair.Quantity,
            UnitCost = repair.UnitCost,
            TotalValue = repair.TotalValue,
            Reason = repair.Reason,
            CreatedAt = repair.CreatedAt,
            CreatedByDirectoryTenantId = repair.CreatedByDirectoryTenantId,
            CreatedByObjectId = repair.CreatedByObjectId
        };
        _db.InventoryCostRepairs.Add(row);
        // Saved here, inside the caller's transaction, because the cost rebuild that follows reads
        // the repair back through the ledger store: an unsaved row would leave the rebuild replaying
        // the unrepaired history.
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(row);
    }

    public async Task<IReadOnlyList<InventoryCostRepairRecord>> ListAsync(
        long productId,
        CancellationToken cancellationToken) =>
        (await _db.InventoryCostRepairs.AsNoTracking()
            .Where(x => x.ProductId == productId)
            // Newest effective repair first; within one instant, the most recently recorded one.
            .OrderByDescending(x => x.EffectiveAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken))
        .Select(ToRecord)
        .ToList();

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private static InventoryCostRepairRecord ToRecord(InventoryCostRepair repair) =>
        new(
            repair.Id,
            repair.ProductId,
            repair.EffectiveAt,
            repair.Quantity,
            repair.UnitCost,
            repair.TotalValue,
            repair.Reason,
            repair.CreatedAt,
            repair.CreatedByDirectoryTenantId,
            repair.CreatedByObjectId);

    private sealed class Transaction(IDbContextTransaction? transaction) : IInventoryCostRepairTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
