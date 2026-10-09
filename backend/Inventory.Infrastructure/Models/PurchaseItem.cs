using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Inventory.Domain.Gst;

namespace Inventory.Infrastructure.Models;

// See Purchase.cs for why the DB table/column naming stays "Receipt" while the CLR type
// is PurchaseItem. ReceiptId is part of the JSON API contract and keeps its name.
public class PurchaseItem : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int ReceiptId { get; set; }
    [JsonIgnore]
    public virtual Purchase? Purchase { get; set; }
    public long ProductId { get; set; }
    public virtual Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }

    /// <summary>
    /// This line's own GST classification and its provenance (issue #429). GST is classified per
    /// line, not once for the whole purchase. Both default to <c>Unknown</c>, which is what the
    /// additive migration leaves on every existing line and what a new line carries until a person
    /// classifies it. Classification is accounting data only: it never affects
    /// <see cref="UnitCost"/>, AVCO, or the restock stock movement this line creates.
    /// </summary>
    public GstClassification GstClassification { get; set; }
    public GstClassificationSource GstClassificationSource { get; set; }

    [JsonIgnore]
    public virtual ICollection<SupplierOrderReceiptAllocation> SupplierOrderAllocations { get; set; } = new List<SupplierOrderReceiptAllocation>();

    [NotMapped]
    public decimal LineTotal => Quantity * UnitCost;
}
