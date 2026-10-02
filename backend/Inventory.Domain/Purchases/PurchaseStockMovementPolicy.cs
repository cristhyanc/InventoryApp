namespace Inventory.Domain.Purchases;

/// <summary>
/// The deterministic facts a purchase line item's restock stock movement carries: its persisted
/// integer quantity change and its total cost. Mirrors the former
/// <c>InventoryApi.Services.PurchaseService.CreatePurchaseMovement</c>'s arithmetic exactly,
/// including the checked cast that throws <see cref="OverflowException"/> for a quantity outside
/// <see cref="int"/> range, unchanged.
/// </summary>
public static class PurchaseStockMovementPolicy
{
    public const string RestockMovementNotes = "Receipt purchase";

    public static int ToStockQuantity(decimal quantity) => checked((int)quantity);

    public static decimal TotalCost(decimal quantity, decimal unitCost) => quantity * unitCost;
}
