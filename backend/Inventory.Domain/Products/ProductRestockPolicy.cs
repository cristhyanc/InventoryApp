namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic low-stock threshold / restock-to invariant shared by product create and
/// update, moved out of the former <c>InventoryApi.Services.ProductService.ValidateRestockSettings</c>
/// (issue #240). The rule and its messages are unchanged.
/// </summary>
public static class ProductRestockPolicy
{
    public const string NegativeLowStockThresholdMessage = "Low Stock Threshold cannot be negative.";
    public const string NegativeRestockToMessage = "Restock To cannot be negative.";
    public const string RestockToBelowThresholdMessage =
        "Restock To must be greater than or equal to the Low Stock Threshold.";

    /// <summary>Returns the validation error message, or <c>null</c> when the settings are valid.</summary>
    public static string? Validate(int lowStockThreshold, int restockTo)
    {
        if (lowStockThreshold < 0) return NegativeLowStockThresholdMessage;
        if (restockTo < 0) return NegativeRestockToMessage;
        if (restockTo < lowStockThreshold) return RestockToBelowThresholdMessage;
        return null;
    }
}
