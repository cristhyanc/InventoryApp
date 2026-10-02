namespace Inventory.Domain.Purchases;

/// <summary>
/// A purchase line's product/quantity/cost shape, as needed to validate it before it is persisted.
/// </summary>
public readonly record struct PurchaseItemCandidate(long ProductId, decimal Quantity, decimal UnitCost);

/// <summary>
/// Validates the format of a purchase's line items: a real product reference, a positive
/// whole-unit quantity, and a non-negative unit cost. Mirrors the former
/// <c>InventoryApi.Services.PurchaseService.ValidateItemsAsync</c> inline check exactly; it does
/// not know whether the referenced products actually exist, which requires a persistence lookup
/// and stays with the Application use case/store.
/// </summary>
public static class PurchaseItemFormatPolicy
{
    public const string InvalidItemsMessage = "Purchase item products, quantities, and costs are invalid.";

    public static bool HasInvalidItem(IEnumerable<PurchaseItemCandidate> items) =>
        items.Any(item => item.ProductId <= 0 || item.Quantity <= 0 || item.UnitCost < 0 ||
            item.Quantity != decimal.Truncate(item.Quantity));
}
