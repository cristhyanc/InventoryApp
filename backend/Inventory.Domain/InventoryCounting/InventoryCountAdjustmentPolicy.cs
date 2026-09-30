using Inventory.Domain.Exceptions;

namespace Inventory.Domain.InventoryCounting;

/// <summary>
/// What a physical inventory count implies for storage stock (issue #245): no movement when the
/// count matches, an increase toward the existing positive physical-stock/restock movement when the
/// count is higher, or a decrease toward the existing Correction movement when the count is lower.
/// Never <c>MachineRefill</c> - that reason is reserved for an internal transfer to a machine, which
/// a storage count never is.
/// </summary>
public enum InventoryCountMovementKind
{
    None,
    Increase,
    Decrease
}

/// <summary>
/// The deterministic outcome of comparing a counted quantity to the authoritative current quantity.
/// <see cref="QuantityChange"/> is signed exactly as <c>StockAdjustment.QuantityChange</c> is
/// elsewhere in this application: positive for an increase, negative for a decrease, zero when
/// <see cref="Kind"/> is <see cref="InventoryCountMovementKind.None"/>.
/// </summary>
public readonly record struct InventoryCountAdjustmentPlan(InventoryCountMovementKind Kind, int QuantityChange);

/// <summary>
/// Take Inventory's difference rule (issue #245): <c>Adjustment = CountedStock - CurrentStock</c>.
/// Pure and deterministic - it never reads or writes persisted state; the caller supplies the
/// authoritative current quantity it already re-read at the mutation boundary.
/// </summary>
public static class InventoryCountAdjustmentPolicy
{
    public static InventoryCountAdjustmentPlan Resolve(int currentStock, int countedStock)
    {
        if (countedStock < 0)
            throw new DomainValidationException("Counted stock cannot be negative.");

        var difference = countedStock - currentStock;
        return difference switch
        {
            0 => new InventoryCountAdjustmentPlan(InventoryCountMovementKind.None, 0),
            > 0 => new InventoryCountAdjustmentPlan(InventoryCountMovementKind.Increase, difference),
            _ => new InventoryCountAdjustmentPlan(InventoryCountMovementKind.Decrease, difference)
        };
    }
}
