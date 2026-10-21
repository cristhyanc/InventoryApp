namespace Inventory.Domain.Reporting.Dashboard;

/// <summary>
/// Total business-owned inventory value and its completeness, derived from each product's
/// already-persisted <c>InventoryValue</c> (the perpetual AVCO valuation the Application
/// <c>RebuildProductCost</c> use case maintains) - never from selling price. A product's inventory value is <c>null</c> only when it
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

    /// <summary>
    /// The unit cost a product may be <em>shown</em> at, from exactly the completeness evidence
    /// <see cref="Summarize"/> counts (issue #788, B of #786): a <see langword="null"/>
    /// <paramref name="inventoryValue"/> is the recorded fact that no cost rebuild has ever run for
    /// this product, so there is no verified cost and the answer is <see langword="null"/> -
    /// never <c>0</c>, which would read as "this product costs nothing".
    ///
    /// <para>Otherwise the answer is the persisted <paramref name="averageUnitCost"/> exactly as
    /// the AVCO rebuild left it, <b>including a genuine zero</b>. This is deliberately not a second
    /// AVCO calculation and not a second completeness rule: it is the one-row form of the question
    /// the Dashboard's unknown-cost count already asks of the whole catalogue, so a product cannot
    /// be counted as unknown there and shown a cost here.</para>
    ///
    /// <para><c>AverageUnitCost</c> is a non-nullable column, so an uncosted product can still
    /// carry a number in it; that number is why this rule reads the inventory value instead of the
    /// cost itself.</para>
    /// </summary>
    public static decimal? DisplayUnitCost(decimal? inventoryValue, decimal averageUnitCost) =>
        inventoryValue is null ? null : averageUnitCost;
}
