using Inventory.Application.InventoryCounting;
using Inventory.Domain.InventoryCounting;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryCountAdjustmentStore"/> (issue #245). It
/// lives in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>
/// and the existing inventory/costing movement services, following the same pattern as
/// <see cref="EfMachineStockEventStore"/>. Applying a movement deliberately reuses
/// <see cref="IInventoryCostService.ApplyMovement"/> rather than writing its own, so a Take Inventory
/// increase inherits exactly the established positive-Restock costing/audit behavior and a decrease
/// inherits the established Correction behavior; it never uses <see cref="StockAdjustmentReason.MachineRefill"/>.
/// The restock-cost suggestion is the same one an operator-entered positive Restock already offers
/// (<see cref="IStockService.GetRestockCostSuggestion"/>), reused rather than reimplemented.
/// </summary>
public sealed class EfInventoryCountAdjustmentStore : IInventoryCountAdjustmentStore
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostService _costing;
    private readonly IInventoryCostRebuildService _rebuild;
    private readonly IStockService _stockService;

    public EfInventoryCountAdjustmentStore(
        AppDbContext db, IInventoryCostService costing, IInventoryCostRebuildService rebuild, IStockService stockService)
    {
        _db = db;
        _costing = costing;
        _rebuild = rebuild;
        _stockService = stockService;
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
        var suggestion = await _stockService.GetRestockCostSuggestion(productId);
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
            var adjustment = _costing.ApplyMovement(productId, quantityChange, reason, null, "Take Inventory count", unitCost);
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
