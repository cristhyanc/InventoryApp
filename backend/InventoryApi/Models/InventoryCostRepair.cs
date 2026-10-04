using System.Text.Json.Serialization;

namespace InventoryApi.Models;

/// <summary>
/// One append-only, costing-only historical repair for one product (issue #359).
///
/// It records that costing inventory the business genuinely held was never valued in the ledger -
/// an incomplete opening or acquisition history - and restores it so the weighted-average replay
/// can cost the sales that depend on it. It is purely a costing event: it never changes
/// <see cref="Product.QuantityInStock"/>, machine quantities, <see cref="StockAdjustment"/> rows or
/// MachineRefill history, which is what separates it from every physical movement.
///
/// The row is immutable. There is no update or delete path anywhere in the application: a repair
/// that turns out to be wrong is a historical fact that was recorded, and correcting the ledger
/// after it is a separate, explicit decision. That is why the creating identity and timestamp are
/// stored with it - a change of this kind to historical COGS has to stay attributable.
/// </summary>
public class InventoryCostRepair : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    public long ProductId { get; set; }

    [JsonIgnore]
    public virtual Product? Product { get; set; }

    /// <summary>
    /// The UTC instant the repaired costing inventory is treated as acquired at. It must fall after
    /// the product's inventory-cost transition cutoff, if it has one, because the replay ignores
    /// everything at or before that cutoff.
    /// </summary>
    public DateTime EffectiveAt { get; set; }

    /// <summary>The costing quantity the repair adds. Always positive.</summary>
    public int Quantity { get; set; }

    /// <summary>The unit cost the repaired quantity is valued at. Never negative; zero is allowed.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>
    /// <see cref="Quantity"/> * <see cref="UnitCost"/>, stored rather than derived so the value the
    /// operator approved stays readable exactly as it was applied.
    /// </summary>
    public decimal TotalValue { get; set; }

    /// <summary>Why the repair was needed. Required, and checked against placeholders by the domain policy.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The Entra directory tenant id (<c>tid</c>) of the operator who applied the repair, following
    /// <see cref="BusinessMembership"/>: the validated <c>(tid, oid)</c> pair is this application's
    /// identity for an actor, and no email address or display name is stored, because those are
    /// mutable, reassignable personal data.
    /// </summary>
    public string CreatedByDirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (<c>oid</c>) of the operator who applied the repair.</summary>
    public string CreatedByObjectId { get; set; } = string.Empty;
}
