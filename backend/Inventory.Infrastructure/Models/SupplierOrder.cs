using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Inventory.Domain.SupplierOrders;

namespace Inventory.Infrastructure.Models;

public enum SupplierOrderStatus
{
    Ordered,
    PartiallyReceived,
    Received,
    Cancelled,

    /// <summary>
    /// Explicitly completed with part of the order never delivered (issue #974). Mirrors
    /// <see cref="Inventory.Domain.SupplierOrders.SupplierOrderStatus.ClosedPartiallyDelivered"/>;
    /// the persisted integer is <c>4</c> and no existing row is ever reclassified into it.
    /// </summary>
    ClosedPartiallyDelivered
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

    /// <summary>
    /// The UTC instant an operator decided no more of this order would arrive (issue #974), set
    /// exactly when <see cref="Status"/> becomes
    /// <see cref="SupplierOrderStatus.ClosedPartiallyDelivered"/> and never cleared. <c>null</c> for
    /// every other order, including every order recorded before that decision existed: nothing is
    /// backfilled.
    /// </summary>
    public DateTime? CompletedWithUndeliveredAt { get; set; }

    /// <summary>
    /// The Entra directory tenant id (<c>tid</c>) of the operator who completed the order, following
    /// <see cref="InventoryCostRepair.CreatedByDirectoryTenantId"/>: the validated <c>(tid, oid)</c>
    /// pair is this application's identity for an actor, and no email address or display name is
    /// stored. Never serialised out.
    /// </summary>
    [JsonIgnore]
    public string? CompletedByDirectoryTenantId { get; set; }

    /// <summary>The Entra object id (<c>oid</c>) of the operator who completed the order. Never serialised out.</summary>
    [JsonIgnore]
    public string? CompletedByObjectId { get; set; }

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

    /// <summary>
    /// How much of this line will never be delivered (issue #974), from the same authoritative
    /// policy the API-owned <c>SupplierOrderLineResponse</c> uses, exactly as
    /// <see cref="OutstandingQuantity"/> does.
    /// </summary>
    [NotMapped]
    public decimal UndeliveredQuantity => SupplierOrderLineOutstandingPolicy.Undelivered(
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
