namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic reorder-alert rules for one product, moved out of the computed properties on
/// the <c>InventoryApi.Models.Product</c> persistence entity (issue #240). The formulas are
/// unchanged; this is now the one authoritative implementation, called both by
/// <c>Inventory.Application.Products.ListLowStockProducts</c> (to decide which products alert and in
/// what order) and by the entity's own computed properties (which the API still serialises), so the
/// alert a caller sees and the alert the use case selected can never diverge.
///
/// Physical storage stock, outstanding supplier orders and current machine replenishment need are
/// all accounted for, per AGENTS.md § Inventory and historical costing invariants.
/// </summary>
public static class ProductReorderPolicy
{
    /// <summary>
    /// Storage stock already on hand, plus stock on an outstanding supplier order, minus what the
    /// machines currently need refilled from storage.
    /// </summary>
    public static decimal ProjectedStockForReorder(
        int quantityInStock, decimal onOrderQuantity, int machineReplenishmentNeed) =>
        quantityInStock + onOrderQuantity - machineReplenishmentNeed;

    /// <summary>
    /// How much to order now: nothing while projected stock is still above the low-stock threshold,
    /// otherwise enough to reach the restock target plus the machines' current need, net of stock on
    /// hand and already on order. The threshold comparison is inclusive (at the threshold still
    /// triggers a reorder).
    /// </summary>
    public static decimal NeedToOrder(
        int quantityInStock,
        decimal onOrderQuantity,
        int machineReplenishmentNeed,
        int lowStockThreshold,
        int restockTo) =>
        ProjectedStockForReorder(quantityInStock, onOrderQuantity, machineReplenishmentNeed) > lowStockThreshold
            ? 0m
            : Math.Max(0m, restockTo + machineReplenishmentNeed - quantityInStock - onOrderQuantity);

    /// <summary>Low storage stock on its own, independent of orders and machine need.</summary>
    public static bool IsLowStock(int quantityInStock, int lowStockThreshold) =>
        quantityInStock <= lowStockThreshold;

    /// <summary>An inactive product never alerts, however low its stock is.</summary>
    public static bool IsReorderAlert(bool isActive, decimal needToOrder) => isActive && needToOrder > 0m;
}
