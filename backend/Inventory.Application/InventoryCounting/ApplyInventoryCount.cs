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
///
/// <para><strong>The read, the comparison and the mutation are one operation</strong> (issue
/// #1241). The write transaction is opened first - <c>BEGIN IMMEDIATE</c> on SQLite, so the write
/// lock is held from before the read until the commit - because "has this quantity changed since the
/// operator counted it?" is a check-then-write rule and a check-then-write rule that is not
/// serialised is not a rule. Reading first and locking at the write let another writer commit in
/// between, and the stale difference was then applied to a quantity nobody counted: stock 10,
/// counted 8, a refill of 4 committed in the gap, and storage left at 12. Now a change can only
/// land before the read, where it is answered with the same <see cref="DomainConflictException"/>
/// (409) and nothing is recorded.</para>
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
        await using var transaction = await _store.BeginWriteTransactionAsync(cancellationToken);

        // Authoritative, inside the write lock: this is the quantity the count is compared with and
        // the one its difference is applied to.
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
            // Nothing was written, so there is nothing to commit: the transaction's disposal releases
            // the lock. The confirmation is still the quantity read under it.
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
        await transaction.CommitAsync(cancellationToken);

        return new InventoryCountApplyResultDto(
            InventoryCountApplyOutcome.Applied, plan.QuantityChange, application.QuantityInStock, application.StockAdjustmentId);
    }
}
