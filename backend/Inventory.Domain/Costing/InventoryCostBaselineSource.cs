namespace Inventory.Domain.Costing;

/// <summary>
/// Whether an inventory-cost transition baseline's opening average unit cost is authoritative or
/// estimated. Mirrors <c>Inventory.Infrastructure.Models.InventoryCostBaselineSource</c> member-for-member and
/// value-for-value (issue #298, child 4 of #149), so Domain/Application never reference the
/// InventoryApi persistence enum directly - the same convention
/// <see cref="Stock.StockAdjustmentReason"/> already established. These numeric values are
/// persisted on <c>InventoryCostTransitionBaseline.CostSource</c>, serialised in stored preview
/// snapshots and exchanged as numbers over the API, so they must not change.
/// </summary>
public enum InventoryCostBaselineSource
{
    ManualAuthoritative = 1,
    ManualEstimated = 2
}
