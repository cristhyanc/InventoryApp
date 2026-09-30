namespace Inventory.Application.InventoryCounting;

/// <summary>A product's identity and authoritative physical storage quantity, as read at the mutation boundary.</summary>
public record InventoryCountProduct(long ProductId, string ProductName, int QuantityInStock);

/// <summary>The persisted result of applying a non-zero inventory-count movement.</summary>
public record InventoryCountAdjustmentApplication(int StockAdjustmentId, int QuantityInStock);

/// <summary>An operator's Take Inventory Apply request for one product (issue #245).</summary>
public record InventoryCountApplyRequestDto(int CountedStock, int ExpectedCurrentStock);

public enum InventoryCountApplyOutcome
{
    /// <summary>Counted matched the authoritative current quantity: no stock movement was created.</summary>
    Confirmed,

    /// <summary>A non-zero difference was applied through the existing Restock/Correction movement.</summary>
    Applied
}

public record InventoryCountApplyResultDto(
    InventoryCountApplyOutcome Outcome,
    int QuantityChange,
    int CurrentStock,
    int? StockAdjustmentId);
