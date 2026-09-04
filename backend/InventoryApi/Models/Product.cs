using System.ComponentModel.DataAnnotations.Schema;

namespace InventoryApi.Models;

public class Product
{
    public Product Clone() => (Product)MemberwiseClone();

    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal AverageUnitCost { get; set; }
    [NotMapped]
    public decimal MachinePrice { get; set; }
    [NotMapped]
    public decimal CommissionValue { get; set; }
    [NotMapped]
    public decimal SuggestedNetValue { get; set; }
    [NotMapped]
    public decimal SuggestedPriceValue { get; set; }
    [NotMapped]
    public int? MdbCode { get; set; }
    [NotMapped]
    public int? MaxStockInMachine { get; set; }
    public int QuantityInStock { get; set; }
    public int LowStockThreshold { get; set; } = 0;
    public string? Unit { get; set; } = "unit";
    public bool Mapped { get; set; }
    public bool IsActive { get; set; } = true;
    [NotMapped]
    public DateTime? LastEatBefore1 { get; set; }
    [NotMapped]
    public DateTime? LastEatBefore2 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public long? CategoryId { get; set; }
    public virtual Category? Category { get; set; }

    public int? SupplierId { get; set; }
    public virtual Supplier? Supplier { get; set; }

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public bool IsLowStock => QuantityInStock <= LowStockThreshold;
}
