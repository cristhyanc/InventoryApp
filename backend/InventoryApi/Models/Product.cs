using System.ComponentModel.DataAnnotations.Schema;

using System.Text.Json.Serialization;

using Inventory.Domain.Products;

namespace InventoryApi.Models;

public class Product : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public Product Clone() => (Product)MemberwiseClone();

    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal AverageUnitCost { get; set; }
    public int? CostingQuantity { get; set; }
    public decimal? InventoryValue { get; set; }
    [NotMapped]
    public decimal MachinePrice { get; set; }
    [NotMapped]
    // Raw Nayax metadata only. Application commission calculations use SiteCommissionAgreement.
    public decimal CommissionValue { get; set; }
    [NotMapped]
    public decimal? SuggestedNetValue { get; set; }
    [NotMapped]
    public decimal? SuggestedPriceValue { get; set; }
    [NotMapped]
    public int? MdbCode { get; set; }
    [NotMapped]
    public int? MaxStockInMachine { get; set; }
    [NotMapped]
    public int MachineReplenishmentNeed { get; set; }
    [NotMapped]
    public decimal OnOrderQuantity { get; set; }
    [NotMapped]
    public decimal ProjectedStockForReorder =>
        ProductReorderPolicy.ProjectedStockForReorder(QuantityInStock, OnOrderQuantity, MachineReplenishmentNeed);
    public int QuantityInStock { get; set; }
    public int LowStockThreshold { get; set; } = 0;
    public int RestockTo { get; set; }
    [NotMapped]
    public decimal NeedToOrder => ProductReorderPolicy.NeedToOrder(
        QuantityInStock, OnOrderQuantity, MachineReplenishmentNeed, LowStockThreshold, RestockTo);
    public string? Unit { get; set; } = "unit";
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

    public bool IsLowStock => ProductReorderPolicy.IsLowStock(QuantityInStock, LowStockThreshold);
    public bool IsReorderAlert => ProductReorderPolicy.IsReorderAlert(IsActive, NeedToOrder);
}
