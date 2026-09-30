using Inventory.Domain.Exceptions;
using Inventory.Domain.InventoryCounting;

namespace Inventory.Application.InventoryCounting;

/// <summary>
/// The Take Inventory apply use case (issue #245): confirms/applies one product's physical count.
/// Re-reads the authoritative current quantity at the mutation boundary and refuses to apply a
/// count based on stale UI state instead of silently overwriting a concurrent change. A zero
/// difference is confirmed with no persisted movement; a non-zero difference reuses the existing
/// positive Restock movement (increase) or Correction movement (decrease) - never
/// <see cref="InventoryCountMovementKind.None"/> and never <c>MachineRefill</c>.
/// </summary>
public sealed class ApplyInventoryCount
{
    private readonly IInventoryCountAdjustmentStore _store;

    public ApplyInventoryCount(IInventoryCountAdjustmentStore store)
    {
        _store = store;
    }

    /// <summary>Null when the product does not exist (or is not owned by the caller's business).</summary>
    public async Task<InventoryCountApplyResultDto?> Handle(
        long productId, InventoryCountApplyRequestDto request, CancellationToken cancellationToken)
    {
        var product = await _store.GetCurrentStockAsync(productId, cancellationToken);
        if (product is null)
            return null;

        if (product.QuantityInStock != request.ExpectedCurrentStock)
        {
            throw new DomainConflictException(
                $"Current stock for '{product.ProductName}' has changed to {product.QuantityInStock} " +
                "since this count was started. Refresh and recount.");
        }

        var plan = InventoryCountAdjustmentPolicy.Resolve(product.QuantityInStock, request.CountedStock);
        if (plan.Kind == InventoryCountMovementKind.None)
        {
            return new InventoryCountApplyResultDto(
                InventoryCountApplyOutcome.Confirmed, QuantityChange: 0, product.QuantityInStock, StockAdjustmentId: null);
        }

        decimal? unitCost = null;
        if (plan.Kind == InventoryCountMovementKind.Increase)
        {
            unitCost = await _store.GetRestockUnitCostAsync(productId, cancellationToken);
            if (unitCost is null)
            {
                throw new DomainValidationException(
                    $"No purchase or average cost is available for '{product.ProductName}'. Record a " +
                    "purchase cost for this product before applying a positive count difference.");
            }
        }

        var application = await _store.ApplyAsync(productId, plan.Kind, plan.QuantityChange, unitCost, cancellationToken);
        return new InventoryCountApplyResultDto(
            InventoryCountApplyOutcome.Applied, plan.QuantityChange, application.QuantityInStock, application.StockAdjustmentId);
    }
}
