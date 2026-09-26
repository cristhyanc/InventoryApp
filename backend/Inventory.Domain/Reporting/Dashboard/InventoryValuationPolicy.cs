namespace Inventory.Domain.Reporting.Dashboard;

/// <summary>
/// Total business-owned inventory value and its completeness, derived from each product's
/// already-persisted <c>InventoryValue</c> (the perpetual AVCO valuation InventoryCostRebuildService
/// maintains) - never from selling price. A product's inventory value is <c>null</c> only when it
/// has never had a cost rebuild run for it, which is a genuinely unknown cost, not a zero one.
/// </summary>
public readonly record struct InventoryValuationResult(
    decimal? TotalInventoryValue,
    bool IsComplete,
    int ProductsWithUnknownCost,
    int TotalProducts);

/// <summary>
/// Deterministic aggregation of per-product inventory values. Missing cost data on any product
/// makes the total unavailable rather than silently summing only the known ones as if nothing were
/// missing - the same "never turn missing cost into zero" rule the COGS/profit invariants already
/// apply to sales.
/// </summary>
public static class InventoryValuationPolicy
{
    public static InventoryValuationResult Summarize(IReadOnlyCollection<decimal?> productInventoryValues)
    {
        var unknownCount = productInventoryValues.Count(v => v is null);
        var isComplete = unknownCount == 0;
        var total = isComplete ? productInventoryValues.Sum(v => v ?? 0m) : (decimal?)null;

        return new InventoryValuationResult(total, isComplete, unknownCount, productInventoryValues.Count);
    }
}
