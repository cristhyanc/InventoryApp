using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace InventoryApi.Models;

// See Receipt.cs (Purchase) for why this file/DB-column naming stays "Receipt" while the
// CLR type is PurchaseItem. ReceiptId is part of the JSON API contract and keeps its name.
public class PurchaseItem
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    [JsonIgnore]
    public virtual Purchase? Purchase { get; set; }
    public long ProductId { get; set; }
    public virtual Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    [JsonIgnore]
    public virtual ICollection<SupplierOrderReceiptAllocation> SupplierOrderAllocations { get; set; } = new List<SupplierOrderReceiptAllocation>();

    [NotMapped]
    public decimal LineTotal => Quantity * UnitCost;
}
