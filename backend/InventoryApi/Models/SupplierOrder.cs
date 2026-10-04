using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Inventory.Domain.SupplierOrders;

namespace InventoryApi.Models;

public enum SupplierOrderStatus
{
    Ordered,
    PartiallyReceived,
    Received,
    Cancelled
}

public class SupplierOrder : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int? SupplierId { get; set; }
    public virtual Supplier? Supplier { get; set; }
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
    public DateTime? ExpectedDate { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public SupplierOrderStatus Status { get; set; } = SupplierOrderStatus.Ordered;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public virtual ICollection<SupplierOrderLine> Lines { get; set; } = new List<SupplierOrderLine>();
}

public class SupplierOrderLine : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int SupplierOrderId { get; set; }
    [JsonIgnore]
    public virtual SupplierOrder SupplierOrder { get; set; } = null!;
    public long ProductId { get; set; }
    public virtual Product Product { get; set; } = null!;
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? Notes { get; set; }
    [JsonIgnore]
    public virtual ICollection<SupplierOrderReceiptAllocation> ReceiptAllocations { get; set; } = new List<SupplierOrderReceiptAllocation>();

    /// <summary>
    /// Delegates to the one authoritative rule in
    /// <see cref="Inventory.Domain.SupplierOrders.SupplierOrderLineOutstandingPolicy"/>, which the
    /// API-owned <c>SupplierOrderLineResponse</c> clients actually receive also uses (issue #304),
    /// the same arrangement <see cref="Product.NeedToOrder"/> has with
    /// <c>Inventory.Domain.Products.ProductReorderPolicy</c>. The value is unchanged.
    /// </summary>
    [NotMapped]
    public decimal OutstandingQuantity => SupplierOrderLineOutstandingPolicy.Outstanding(
        (Inventory.Domain.SupplierOrders.SupplierOrderStatus)SupplierOrder.Status, QuantityOrdered, QuantityReceived);
}

public class SupplierOrderReceiptAllocation : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int SupplierOrderLineId { get; set; }
    [JsonIgnore]
    public virtual SupplierOrderLine SupplierOrderLine { get; set; } = null!;
    public int ReceiptItemId { get; set; }
    [JsonIgnore]
    public virtual PurchaseItem ReceiptItem { get; set; } = null!;
    public decimal QuantityApplied { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
