using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class ReceiptItem
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    [JsonIgnore]
    public virtual Receipt? Receipt { get; set; }
    public long ProductId { get; set; }
    public virtual Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }

    [NotMapped]
    public decimal LineTotal => Quantity * UnitCost;
}
