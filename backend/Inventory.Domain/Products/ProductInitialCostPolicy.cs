namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic invariant on a new product's initial unit cost, moved out of the former
/// <c>InventoryApi.Services.ProductService.Create</c> (issue #240). The rule and its message are
/// unchanged.
/// </summary>
public static class ProductInitialCostPolicy
{
    public const string NegativeInitialUnitCostMessage = "Initial unit cost cannot be negative.";

    /// <summary>Returns the validation error message, or <c>null</c> when the cost is valid.</summary>
    public static string? Validate(decimal? initialUnitCost) =>
        initialUnitCost is < 0 ? NegativeInitialUnitCostMessage : null;
}
