using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

public enum StockAdjustmentReason
{
    Restock = 0,
    Sale = 1,
    Damaged = 2,
    Expired = 3,
    Correction = 4,
    MachineRefill = 5
}

/// <summary>
/// Distinguishes an operator-entered movement from one imported and applied from a Nayax
/// machine-stock event (issue #183), so manual and Nayax-sourced refills remain distinguishable in
/// the audit trail even though both use <see cref="StockAdjustmentReason.MachineRefill"/>.
/// </summary>
public enum StockAdjustmentSource
{
    Manual = 0,
    Nayax = 1
}

public class StockAdjustment : IBusinessOwned
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
    public int? ReceiptItemId { get; set; }
    [JsonIgnore]
    public virtual PurchaseItem? ReceiptItem { get; set; }

    // Positive = stock added, Negative = stock removed
    public int QuantityChange { get; set; }
    public int QuantityAfter { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? TotalCost { get; set; }
    public int? CostingQuantityAfter { get; set; }
    public decimal? AverageUnitCostAfter { get; set; }
    public decimal? InventoryValueAfter { get; set; }

    public StockAdjustmentReason Reason { get; set; } = StockAdjustmentReason.MachineRefill;
    public StockAdjustmentSource Source { get; set; } = StockAdjustmentSource.Manual;
    /// <summary>
    /// The machine a refill went to, or <c>null</c> for a movement that names no machine: a local
    /// machine, <c>Machines.Id</c> (issue #741), never the provider's machine number. The pair
    /// <c>(BusinessId, MachineId)</c> is a foreign key on the machines' <c>(BusinessId, Id)</c> key,
    /// restricted on delete; the <c>RepointStockMachineReferences</c> migration moved every stored
    /// movement onto its machine's local row. A Nayax Sync Restock event keeps the provider's number
    /// on <see cref="NayaxMachineStockEvent.MachineId"/>; the movement it produces records the local
    /// machine.
    /// </summary>
    public long? MachineId { get; set; }
    public string? Notes { get; set; }

    // Optional eat-before / expiration date for this adjustment (applies to restocks)
    public DateTime? EatBefore { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The instant the <em>machine</em>-stock ledger replays this movement at, when it is not the
    /// instant the movement was recorded (issue #1243). It is <c>null</c> for every movement but a
    /// refill applied from a Nayax Sync Restock event, where it holds that event's own
    /// <c>EventDateTimeGMT</c> - the instant the machine was physically refilled, which may be days
    /// before an operator applied it.
    ///
    /// <para><strong>Only <see cref="Inventory.Infrastructure.Persistence.EfMachineStockLedgerStore"/>
    /// reads it.</strong> <see cref="EffectiveAt"/> stays the apply time and remains the one instant
    /// the cost replay, Stock History and every storage-stock query use, so applying a late event
    /// cannot take units out of storage at an earlier instant and drive physical stock negative
    /// there. A machine count sets the machine's expected level from its own instant onward, so
    /// without this column a refill the count had already absorbed was replayed on top of it and
    /// invented machine stock - and the next comparison reported shrinkage that never happened.</para>
    ///
    /// <para>Nullable with no backfill: a movement recorded before this column existed keeps today's
    /// behaviour, because the ledger falls back to <see cref="EffectiveAt"/>.</para>
    ///
    /// <para>Integration state, like <see cref="LocalMachine.ExternalMachineId"/>: it is derived from
    /// the provider's own event, no request chooses it, and it is deliberately off every wire
    /// contract. The movement's published instants stay <see cref="CreatedAt"/> and
    /// <see cref="EffectiveAt"/>, so Stock History and the adjust response are byte-for-byte what
    /// they were.</para>
    /// </summary>
    [JsonIgnore]
    public DateTime? MachineLedgerEffectiveAt { get; set; }
}
