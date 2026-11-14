using Inventory.Application.Costing;
using Inventory.Application.InventoryCounting;
using Inventory.Application.Stock;
using Inventory.Domain.InventoryCounting;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IInventoryCountAdjustmentStore"/> (issue #245). It lives
/// in Inventory.Infrastructure, following the same pattern as
/// <see cref="EfMachineStockEventStore"/>: <see cref="AppDbContext"/> and the persistence models it
/// depends on moved there in issue #307, and this adapter family followed them in issue #309
/// (Persistence 8/8 of #153).
/// Applying a movement deliberately reuses the Application <see cref="IRecordInventoryMovement"/> and
/// <see cref="IRebuildProductCost"/> use cases (issue #296) rather than writing its own, so a Take
/// Inventory increase inherits exactly the established positive-Restock costing/audit behavior and a
/// decrease inherits the established Correction behavior; it never uses
/// <see cref="StockAdjustmentReason.MachineRefill"/>.
/// The restock-cost suggestion is the same authoritative use case an operator-entered positive
/// Restock already uses (<see cref="IGetRestockCostSuggestion"/>, issue #282), reused rather than
/// reimplemented.
///
/// <para>The transaction the apply runs in is <see cref="SqliteWriteLockTransaction"/>
/// (<c>BEGIN IMMEDIATE</c>) and it is opened by the use case, before the quantity the count is
/// decided from is read (issue #1241). <see cref="ApplyAsync"/> therefore saves inside that
/// transaction and does not commit it.</para>
/// </summary>
public sealed class EfInventoryCountAdjustmentStore : IInventoryCountAdjustmentStore
{
    private readonly AppDbContext _db;
    private readonly IRecordInventoryMovement _recordMovement;
    private readonly IRebuildProductCost _rebuild;
    private readonly IGetRestockCostSuggestion _getRestockCostSuggestion;

    public EfInventoryCountAdjustmentStore(
        AppDbContext db, IRecordInventoryMovement recordMovement, IRebuildProductCost rebuild, IGetRestockCostSuggestion getRestockCostSuggestion)
    {
        _db = db;
        _recordMovement = recordMovement;
        _rebuild = rebuild;
        _getRestockCostSuggestion = getRestockCostSuggestion;
    }

    /// <inheritdoc />
    public async Task<IInventoryCountWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken) =>
        new WriteTransaction(await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken).ConfigureAwait(false));

    public async Task<InventoryCountProduct?> GetCurrentStockAsync(long productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Name, p.QuantityInStock })
            .FirstOrDefaultAsync(cancellationToken);

        return product is null ? null : new InventoryCountProduct(product.Id, product.Name, product.QuantityInStock);
    }

    public async Task<decimal?> GetRestockUnitCostAsync(long productId, CancellationToken cancellationToken)
    {
        var suggestion = await _getRestockCostSuggestion.Handle(productId, cancellationToken);
        return suggestion?.UnitCost;
    }

    public async Task<InventoryCountAdjustmentApplication> ApplyAsync(
        long productId,
        InventoryCountMovementKind kind,
        int quantityChange,
        decimal? unitCost,
        CancellationToken cancellationToken)
    {
        var reason = kind == InventoryCountMovementKind.Increase
            ? StockAdjustmentReason.Restock
            : StockAdjustmentReason.Correction;

        try
        {
            var adjustment = await _recordMovement.RecordAsync(
                new InventoryMovement(productId, quantityChange, reason, "Take Inventory count", unitCost),
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return new InventoryCountAdjustmentApplication(adjustment.Id, adjustment.QuantityAfter);
        }
        catch
        {
            // The caller's transaction is what discards the writes; this only drops what they left
            // tracked, so a failed apply carries nothing into the rest of the request.
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The transaction handed back to the use case: <see cref="SqliteWriteLockTransaction"/>, which
    /// already owns the rollback-on-dispose a refused count relies on.
    /// </summary>
    private sealed class WriteTransaction(SqliteWriteLockTransaction transaction) : IInventoryCountWriteTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
