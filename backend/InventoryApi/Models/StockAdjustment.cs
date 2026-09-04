using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public enum StockAdjustmentReason
{
    Restock = 0,
    Sale = 1,
    Damaged = 2,
    Expired = 3,
    Correction = 4,
    MachineRefill = 5
}

public class StockAdjustment
{
    public int Id { get; set; }

    public long ProductId { get; set; }
    [JsonIgnore]
    public virtual Product? Product { get; set; }
    public int? ReceiptItemId { get; set; }
    [JsonIgnore]
    public virtual ReceiptItem? ReceiptItem { get; set; }

    // Positive = stock added, Negative = stock removed
    public int QuantityChange { get; set; }
    public int QuantityAfter { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? TotalCost { get; set; }

    public StockAdjustmentReason Reason { get; set; } = StockAdjustmentReason.MachineRefill;
    public long? MachineId { get; set; }
    public string? Notes { get; set; }

    // Optional eat-before / expiration date for this adjustment (applies to restocks)
    public DateTime? EatBefore { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
