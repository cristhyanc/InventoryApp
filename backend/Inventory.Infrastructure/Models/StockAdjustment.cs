using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

public enum StockAdjustmentReason
{
    Restock = 0,
    Sale = 1,
    Damaged = 2,
    Expired = 3,
    Correction = 4,
    MachineRefill = 5
}

/// <summary>
/// Distinguishes an operator-entered movement from one imported and applied from a Nayax
/// machine-stock event (issue #183), so manual and Nayax-sourced refills remain distinguishable in
/// the audit trail even though both use <see cref="StockAdjustmentReason.MachineRefill"/>.
/// </summary>
public enum StockAdjustmentSource
{
    Manual = 0,
    Nayax = 1
}

public class StockAdjustment : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    public long ProductId { get; set; }
    [JsonIgnore]
    public virtual Product? Product { get; set; }
    public int? ReceiptItemId { get; set; }
    [JsonIgnore]
    public virtual PurchaseItem? ReceiptItem { get; set; }

    // Positive = stock added, Negative = stock removed
    public int QuantityChange { get; set; }
    public int QuantityAfter { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? TotalCost { get; set; }
    public int? CostingQuantityAfter { get; set; }
    public decimal? AverageUnitCostAfter { get; set; }
    public decimal? InventoryValueAfter { get; set; }

    public StockAdjustmentReason Reason { get; set; } = StockAdjustmentReason.MachineRefill;
    public StockAdjustmentSource Source { get; set; } = StockAdjustmentSource.Manual;
    public long? MachineId { get; set; }
    public string? Notes { get; set; }

    // Optional eat-before / expiration date for this adjustment (applies to restocks)
    public DateTime? EatBefore { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;
}
