using System.ComponentModel.DataAnnotations.Schema;

using System.Text.Json.Serialization;

using Inventory.Domain.Gst;
using Inventory.Domain.Products;

namespace Inventory.Infrastructure.Models;

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

    /// <summary>
    /// This product's identifier in the business's sales provider, or <c>null</c> when the provider
    /// does not name it (issue #759, parent #329 decision P1 as revised on 18 October 2026).
    ///
    /// <strong>Opaque text, issued by <c>Business.SalesProvider</c>.</strong> It is stored as written
    /// and compared exactly; shared code never parses it and never assumes it is numeric, which is
    /// what lets a provider that issues a code or a GUID need no schema change. For Nayax it holds
    /// the <c>NayaxProductID</c> and is written only through
    /// <c>Inventory.Domain.Providers.NayaxExternalId</c>, the one conversion between that numeric id
    /// and this text.
    ///
    /// <strong>Not a local id, in either direction.</strong> <see cref="Id"/> is this application's
    /// own key, assigned by the database on insert since issue #735 (A4) - including for a row the
    /// catalogue import creates - and nothing reads this column as a local id or a local id as this
    /// column. It is unique per business among the rows that have one (a filtered unique index), for
    /// the reason every constraint over an externally supplied value is scoped by business: two
    /// operators' catalogues may legitimately both contain product 100.
    ///
    /// Deliberately off the wire, like <see cref="GstRule"/>: this entity's serialisable surface is
    /// a pinned legacy API contract, and a provider's identifier is integration state rather than
    /// part of the catalogue payload.
    /// </summary>
    [JsonIgnore]
    public string? ExternalProductId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }

    // A product carries no selling price (issue #792, A2 of #786). `UnitPrice` was the catalogue
    // default/list price: a display value no calculation ever read, which made it a second, stale
    // answer to "what does this sell for?" beside the machine's own live price. The prices that
    // remain are the ones a real event recorded - `MachinePrice` from the per-machine Nayax
    // RetailPrice below, a purchase item's UnitCost, a supplier order line's agreed UnitPrice and
    // the amount on a recorded sale (docs/architecture.md § A product carries no selling price).
    public decimal AverageUnitCost { get; set; }
    public int? CostingQuantity { get; set; }
    public decimal? InventoryValue { get; set; }

    /// <summary>
    /// The version of the three costing values above (issue #1245). It starts at <c>0</c> and moves
    /// on by one in the <em>same transaction</em> as every rebuilt costing position, because the one
    /// place that stages such a position - <c>EfInventoryCostLedgerStore.StageProductPosition</c> -
    /// moves it. Every stock write, purchase create, purchase edit, purchase delete, Take Inventory
    /// count, refill and costing repair ends in a rebuild, so each of them moves it; a dry run
    /// stages no position and moves nothing.
    ///
    /// <para><strong>Why the product needs one.</strong> The sales cost-rebuild drain replays a
    /// product's pending rebuild <em>outside</em> the write lock and then acknowledges the claim, and
    /// the pending row's own <c>Revision</c> only answers for work <em>another sale</em> added. A
    /// purchase, a correction or a count committed during that replay changes the history the staged
    /// figures were computed from while leaving that row untouched, so without this the drain would
    /// write a costing position that leaves the newer write out until the next rebuild. The drain
    /// records this version when it claims the product and saves nothing when it has moved since (see
    /// <c>EfLatestNayaxSalesStore.AcknowledgeAsync</c>).</para>
    ///
    /// <para>A latest-movement id could not do that job: a purchase edit changes a movement's
    /// quantity, cost or instant <em>in place</em> and adds no new one.</para>
    ///
    /// <para>It records nothing financial - it is a counter of rebuilds, carrying no quantity, cost
    /// or value - and it is deliberately off the wire, like <see cref="GstRule"/>: this entity's
    /// serialisable surface is a pinned legacy API contract, and a concurrency version is internal
    /// bookkeeping rather than part of the catalogue payload.</para>
    /// </summary>
    [JsonIgnore]
    public long CostingVersion { get; set; }
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

    /// <summary>
    /// This product's configured GST rule (issue #430), <see cref="GstRules.None"/> until an
    /// administrator sets one. It is rule configuration, never a classification: it changes no
    /// purchase line, charge, unit cost or inventory value, and it is only read by the explicit
    /// historical Preview/Apply maintenance workflow (issue #433).
    ///
    /// Deliberately off the wire. This entity's serialisable surface is a pinned legacy API
    /// contract - <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> regenerates the
    /// published <c>Product</c> component from it, and <c>InventoryApi.DTOs.ProductResponse</c> is
    /// compared against it byte for byte - so the rule is published on its own
    /// <c>/api/products/{id}/gst-rule</c> resource rather than added to the catalogue payload and
    /// to every nested product snapshot a purchase or supplier-order response carries.
    /// </summary>
    [JsonIgnore]
    public GstClassification GstRule { get; set; } = GstRules.None;
    [NotMapped]
    public DateTime? LastEatBefore1 { get; set; }
    [NotMapped]
    public DateTime? LastEatBefore2 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public long? CategoryId { get; set; }
    public virtual Category? Category { get; set; }

    // A product carries no supplier (issue #787, A1 of #786). A supplier is recorded where it is
    // actually agreed - on the purchase that was bought and on the supplier order that was placed -
    // and the per-product price comparison derives every supplier a product was really bought from
    // out of that purchase history (docs/architecture.md § Supplier product price history and
    // comparison). The stored default was never read by an ordering decision, so it is gone rather
    // than reinterpreted.
    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public bool IsLowStock => ProductReorderPolicy.IsLowStock(QuantityInStock, LowStockThreshold);
    public bool IsReorderAlert => ProductReorderPolicy.IsReorderAlert(IsActive, NeedToOrder);
}
