namespace Inventory.Domain.Reporting.Dashboard;

/// <summary>
/// Total business-owned inventory value and its completeness, derived from each product's
/// already-persisted <c>InventoryValue</c> (the perpetual AVCO valuation the Application
/// <c>RebuildProductCost</c> use case maintains) - never from selling price. A product's inventory
/// value is <c>null</c> only when it has never had a cost rebuild run for it, which is a genuinely
/// unknown cost, not a zero one - unless the product has authoritatively never held inventory at
/// all, which <see cref="ProductInventoryValuationFact"/> is the evidence for.
/// </summary>
/// <param name="TotalInventoryValue">
/// The sum of the known values, or <c>null</c> while any product's inventory value is unknown.
/// </param>
/// <param name="IsComplete">Whether every product that holds inventory has a known value.</param>
/// <param name="ProductsWithUnknownCost">
/// How many products block the valuation - the count a card presents as "missing cost data". A
/// product that has never held inventory is <em>not</em> one of them.
/// </param>
/// <param name="TotalProducts">
/// Every product the valuation was taken over, active and inactive, whether or not it holds
/// anything. Deliberately a different figure from <paramref name="ProductsWithUnknownCost"/>: the
/// catalogue's size is not a data-quality statement.
/// </param>
public readonly record struct InventoryValuationResult(
    decimal? TotalInventoryValue,
    bool IsComplete,
    int ProductsWithUnknownCost,
    int TotalProducts);

/// <summary>
/// What one product contributes to the valuation, and the evidence that decides it (issue #861).
///
/// <para>Before this, a product's <c>null InventoryValue</c> was the only evidence the valuation
/// had, and it cannot tell <em>costed at nothing</em> from <em>never costed</em>: a product created
/// yesterday that has never been bought, stocked, refilled or sold made the whole Dashboard total
/// unavailable, as though the business were holding stock of unknown worth. It is holding nothing.
/// So the valuation is given the rest of the product's position as well.</para>
///
/// <para><strong>Zero storage quantity is deliberately not enough on its own.</strong> Storage stock
/// is the home shelf; units already loaded into a vending machine are business-owned costing
/// inventory that <c>QuantityInStock</c> does not count (AGENTS.md § Inventory and historical costing
/// invariants), so an empty shelf says nothing about what the business owns. Only the absence of
/// <em>every</em> record that inventory was ever in this product does.</para>
/// </summary>
/// <param name="InventoryValue">
/// The persisted <c>Product.InventoryValue</c>: the AVCO valuation, or <c>null</c> when no cost
/// rebuild has ever run for this product.
/// </param>
/// <param name="CostingQuantity">
/// The persisted <c>Product.CostingQuantity</c>: business-owned costing units wherever they sit,
/// including inside a machine, or <c>null</c> when no rebuild has established a position.
/// </param>
/// <param name="QuantityInStock">
/// The persisted <c>Product.QuantityInStock</c>: storage/home stock only, which excludes units in a
/// machine.
/// </param>
/// <param name="HasInventoryHistory">
/// Whether anything recorded ever put inventory into this product, took it out, or valued it: a
/// stock movement (restock, correction, write-off or machine refill), a purchase line, a stored
/// sale, an operator's machine stock count, an inventory-cost transition baseline, a costing repair,
/// or an imported machine stock event - including one still unprocessed, which is an unresolved
/// position rather than an empty one. A supplier order line is deliberately not evidence: ordering
/// is an intention, and the purchase that receives it is what records inventory.
/// </param>
public readonly record struct ProductInventoryValuationFact(
    decimal? InventoryValue,
    int? CostingQuantity,
    int QuantityInStock,
    bool HasInventoryHistory)
{
    /// <summary>
    /// Whether the product's <em>own</em> stored columns leave open that it has never held
    /// inventory: no established valuation, no costing units and an empty storage shelf.
    ///
    /// <para>This is only half the evidence - <see cref="HasInventoryHistory"/> is the other half -
    /// and it is exposed separately so a persistence adapter can narrow the rows it reads to the few
    /// products this can still be true of, without restating the rule. A product it is false for is
    /// valued or unknown on its stored position alone, whatever its history.</para>
    /// </summary>
    public static bool CouldNeverHaveHeldInventory(
        decimal? inventoryValue, int? costingQuantity, int quantityInStock) =>
        inventoryValue is null && costingQuantity is null or 0 && quantityInStock == 0;

    /// <summary>
    /// Whether this product authoritatively holds nothing and never has. Such a product adds no
    /// value to the total and is not counted as missing cost data, because there is no cost to be
    /// missing. Its unit cost stays <em>unknown</em> all the same -
    /// <see cref="InventoryValuationPolicy.DisplayUnitCost"/> still answers <see langword="null"/>
    /// for it - because "we have never bought any" is not a measurement of what one costs.
    /// </summary>
    public bool HasNeverHeldInventory =>
        !HasInventoryHistory
        && CouldNeverHaveHeldInventory(InventoryValue, CostingQuantity, QuantityInStock);
}

/// <summary>
/// Deterministic aggregation of per-product inventory values. Missing cost data on any product that
/// holds inventory makes the total unavailable rather than silently summing only the known ones as
/// if nothing were missing - the same "never turn missing cost into zero" rule the COGS/profit
/// invariants already apply to sales.
///
/// <para>A product that has authoritatively never held inventory is the one thing that is not
/// missing data (issue #861): it contributes <c>0</c> to the total and <c>0</c> to the unknown-cost
/// count, and it is still counted in <see cref="InventoryValuationResult.TotalProducts"/>, because
/// it is in the catalogue. Nothing here fabricates a cost, an opening quantity or a costing history
/// for it, and nothing turns a <c>null</c> inventory value into a zero: the product is excluded from
/// the aggregation, not valued at nothing.</para>
/// </summary>
public static class InventoryValuationPolicy
{
    public static InventoryValuationResult Summarize(
        IReadOnlyCollection<ProductInventoryValuationFact> productFacts)
    {
        ArgumentNullException.ThrowIfNull(productFacts);

        var holdings = productFacts.Where(fact => !fact.HasNeverHeldInventory).ToList();
        var unknownCount = holdings.Count(fact => fact.InventoryValue is null);
        var isComplete = unknownCount == 0;
        var total = isComplete ? holdings.Sum(fact => fact.InventoryValue ?? 0m) : (decimal?)null;

        return new InventoryValuationResult(total, isComplete, unknownCount, productFacts.Count);
    }

    /// <summary>
    /// The unit cost a product may be <em>shown</em> at, from exactly the per-product costing
    /// evidence <see cref="Summarize"/> reads (issue #788, B of #786): a <see langword="null"/>
    /// <paramref name="inventoryValue"/> is the recorded fact that no cost rebuild has ever run for
    /// this product, so there is no verified cost and the answer is <see langword="null"/> -
    /// never <c>0</c>, which would read as "this product costs nothing".
    ///
    /// <para>Otherwise the answer is the persisted <paramref name="averageUnitCost"/> exactly as
    /// the AVCO rebuild left it, <b>including a genuine zero</b>. This is deliberately not a second
    /// AVCO calculation and not a second completeness rule: it is the one-row form of the question
    /// the Dashboard's unknown-cost count already asks of the whole catalogue.</para>
    ///
    /// <para><c>AverageUnitCost</c> is a non-nullable column, so an uncosted product can still
    /// carry a number in it; that number is why this rule reads the inventory value instead of the
    /// cost itself.</para>
    ///
    /// <para><strong>A product that has never held inventory is unknown here and still not counted
    /// as missing cost data there</strong> (issue #861), and that is not a disagreement between the
    /// two: the catalogue total asks what the business owns, and owning none of something is a
    /// measured <c>0</c>; this asks what a unit of it costs, which nothing has ever established.
    /// Answering <c>0</c> here would invent a cost rather than report an absence, so the rule does
    /// not change - a never-stocked product's cost reads as unknown, exactly as before.</para>
    /// </summary>
    public static decimal? DisplayUnitCost(decimal? inventoryValue, decimal averageUnitCost) =>
        inventoryValue is null ? null : averageUnitCost;
}
