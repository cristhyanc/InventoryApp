using Inventory.Application.Costing;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryMovementStore"/> (issue #296). It still
/// lives in InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and the
/// persistence models it depends on moved there in issue #307, and moving this adapter family after
/// them is Persistence 7/8 and 8/8 of #153.
///
/// It only reads the product (preferring the instance the caller's unit of work already tracks,
/// exactly as the former <c>InventoryCostService.ApplyMovement</c> did) and stages the movement the
/// <see cref="RecordInventoryMovement"/> use case already costed. It never saves, never opens a
/// transaction and applies no costing rule. Business ownership is stamped by
/// <see cref="AppDbContext"/> on save and reads go through its business query filter.
/// </summary>
public sealed class EfInventoryMovementStore : IInventoryMovementStore
{
    private readonly AppDbContext _db;

    public EfInventoryMovementStore(AppDbContext db) => _db = db;

    public async Task<InventoryMovementCostState?> GetCostStateAsync(long productId, CancellationToken cancellationToken)
    {
        var product = await FindTrackedProductAsync(productId, cancellationToken);
        return product is null
            ? null
            : new InventoryMovementCostState(product.QuantityInStock, product.CostingQuantity, product.AverageUnitCost);
    }

    public IStagedInventoryMovement Stage(CostedInventoryMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        var product = _db.Products.Local.FirstOrDefault(p => p.Id == movement.ProductId)
            ?? throw new InvalidOperationException($"Product {movement.ProductId} was not loaded.");

        product.QuantityInStock = movement.QuantityAfter;
        product.UpdatedAt = DateTime.UtcNow;

        var adjustment = new StockAdjustment
        {
            ProductId = movement.ProductId,
            QuantityChange = movement.QuantityChange,
            QuantityAfter = movement.QuantityAfter,
            Reason = (StockAdjustmentReason)movement.Reason,
            Source = (StockAdjustmentSource)movement.Source,
            MachineId = movement.MachineId,
            EatBefore = movement.EatBefore,
            UnitCost = movement.UnitCost,
            TotalCost = movement.TotalCost,
            Notes = movement.Notes,
        };
        _db.StockAdjustments.Add(adjustment);
        return new StagedMovement(adjustment);
    }

    private async ValueTask<Product?> FindTrackedProductAsync(long productId, CancellationToken cancellationToken) =>
        _db.Products.Local.FirstOrDefault(p => p.Id == productId) ??
        await _db.Products.FindAsync([productId], cancellationToken);

    private sealed class StagedMovement(StockAdjustment adjustment) : IStagedInventoryMovement
    {
        public int Id => adjustment.Id;

        public int QuantityAfter => adjustment.QuantityAfter;
    }
}
