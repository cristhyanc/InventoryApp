namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic invariant on a new product's initial unit cost, moved out of the former
/// <c>InventoryApi.Services.ProductService.Create</c> (issue #240).
///
/// <para><strong>Since issue #1238 it is a rule about two fields, not one.</strong> Opening stock
/// above zero is recorded as a costed opening <c>Restock</c> movement, and
/// <c>Inventory.Domain.Costing.WeightedAverageCostReplay</c> treats a positive <c>Restock</c> with
/// no unit cost as a fatal data-quality fault. A create that left the cost empty therefore produced
/// a product whose every later stock write - a pick-list refill, a manual adjustment, a Take
/// Inventory apply, a new purchase - ended in a rebuild that refused the opening movement, and no
/// ordinary screen could repair it. So the cost is required <em>whenever there is opening stock to
/// cost</em>; a create with no opening stock records no movement and keeps the cost optional,
/// exactly as before. Zero is a cost (stock that was free), and only an absent cost is refused.</para>
/// </summary>
public static class ProductInitialCostPolicy
{
    public const string NegativeInitialUnitCostMessage = "Initial unit cost cannot be negative.";

    public const string InitialUnitCostRequiredMessage =
        "Initial Unit Cost is required when Opening Stock is above zero.";

    /// <summary>
    /// Returns the validation error message, or <c>null</c> when the pair is valid. The bound comes
    /// first: a negative cost is out of bounds whatever the opening stock is, and the required rule
    /// is only ever about a cost nobody gave.
    /// </summary>
    public static string? Validate(int openingStock, decimal? initialUnitCost)
    {
        if (initialUnitCost is < 0) return NegativeInitialUnitCostMessage;

        return openingStock > 0 && initialUnitCost is null ? InitialUnitCostRequiredMessage : null;
    }
}
