using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One operator's physical count of one product in one vending machine (issue #56): the only
/// machine-stock fact the application did not already persist, and the baseline the machine-stock
/// ledger counts forward from.
///
/// It is an append-only observation. Nothing in the application updates or deletes a row here, and
/// recording one changes no other row at all - not <c>QuantityInStock</c>, not
/// <c>CostingQuantity</c>, not <c>InventoryValue</c>, no <see cref="StockAdjustment"/> and no sale
/// cost - which is what keeps a machine count out of business-wide costing entirely. The difference
/// a count absorbs is deliberately not stored: <c>Inventory.Domain.MachineStock.MachineStockLedger</c>
/// derives it, so there is one authoritative figure rather than a stored copy that could drift, and
/// a shrinkage is never written away as a correction.
///
/// Refills, returns to storage and completed sales are <em>not</em> stored here. They already exist
/// as <see cref="StockAdjustment"/> rows (reason <c>MachineRefill</c>, with the machine recorded) and
/// <see cref="NayaxSales"/> rows, and the ledger reads those facts, so no event in this ledger is
/// recorded twice and no second copy can fall out of step.
/// </summary>
public class MachineStockCount : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    /// <summary>
    /// The Nayax machine the count was taken on. A remote identity, exactly as
    /// <see cref="StockAdjustment.MachineId"/> is.
    ///
    /// <para>Since issue #740 a machine <em>is</em> persisted (<see cref="LocalMachine"/>), but this
    /// column is deliberately unchanged and still holds the provider's own number: repointing the
    /// stored machine references at <c>Machines.Id</c> is issue #741 (G2).</para>
    /// </summary>
    public long MachineId { get; set; }

    public long ProductId { get; set; }

    [JsonIgnore]
    public virtual Product? Product { get; set; }

    /// <summary>The units the operator counted in the machine. Never negative.</summary>
    public int CountedQuantity { get; set; }

    /// <summary>
    /// The instant the count was recorded, from the server clock, stored as UTC. It is the ledger's
    /// ordering instant: movements after it are replayed on top of the counted level.
    /// </summary>
    public DateTime CountedAtUtc { get; set; }

    /// <summary>The operator's optional note, for example why a discrepancy was expected.</summary>
    public string? Notes { get; set; }
}
