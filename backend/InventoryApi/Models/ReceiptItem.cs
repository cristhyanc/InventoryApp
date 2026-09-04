using System.ComponentModel.DataAnnotations.Schema;

namespace InventoryApi.Models;

public class ReceiptItem
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    public virtual Receipt? Receipt { get; set; }
    public long ProductId { get; set; }
    public virtual Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }

    [NotMapped]
    public decimal LineTotal => Quantity * UnitCost;
}
