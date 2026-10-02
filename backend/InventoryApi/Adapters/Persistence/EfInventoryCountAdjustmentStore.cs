using Inventory.Application.Costing;
using Inventory.Application.InventoryCounting;
using Inventory.Application.Stock;
using Inventory.Domain.InventoryCounting;
using Inventory.Domain.Stock;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryCountAdjustmentStore"/> (issue #245). It
/// lives in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>
/// and the persistence models, following the same pattern as <see cref="EfMachineStockEventStore"/>.
/// Applying a movement deliberately reuses the Application <see cref="IRecordInventoryMovement"/> and
/// <see cref="IRebuildProductCost"/> use cases (issue #296) rather than writing its own, so a Take
/// Inventory increase inherits exactly the established positive-Restock costing/audit behavior and a
/// decrease inherits the established Correction behavior; it never uses
/// <see cref="StockAdjustmentReason.MachineRefill"/>.
/// The restock-cost suggestion is the same authoritative use case an operator-entered positive
/// Restock already uses (<see cref="IGetRestockCostSuggestion"/>, issue #282), reused rather than
/// reimplemented.
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

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var adjustment = await _recordMovement.RecordAsync(
                new InventoryMovement(productId, quantityChange, reason, "Take Inventory count", unitCost),
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return new InventoryCountAdjustmentApplication(adjustment.Id, adjustment.QuantityAfter);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            throw;
        }
    }
}
