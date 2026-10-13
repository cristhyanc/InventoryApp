using Inventory.Domain.Stock;

namespace Inventory.Domain.MachineStock;

/// <summary>
/// What one machine-stock movement is (issue #56). These four kinds are the complete movement
/// vocabulary of the machine-stock ledger, and each one answers two separate questions: what it
/// does to the units physically sitting in the machine, and whether it consumes the
/// business-owned costing inventory the AVCO ledger tracks. The two answers are deliberately
/// independent - that separation is what keeps a refill from creating COGS and keeps a sale from
/// creating it twice.
/// </summary>
public enum MachineStockMovementKind
{
    /// <summary>
    /// Units moved out of storage into the machine: an internal transfer, persisted as a
    /// <see cref="StockAdjustmentReason.MachineRefill"/> movement with a negative storage quantity
    /// change. It reduces storage stock, increases machine stock, and must never change
    /// <c>CostingQuantity</c>/<c>InventoryValue</c> or create COGS.
    /// </summary>
    Refill = 0,

    /// <summary>
    /// The reverse transfer: units taken back out of the machine into storage, persisted as the
    /// same <see cref="StockAdjustmentReason.MachineRefill"/> movement with a <em>positive</em>
    /// storage quantity change. It increases storage stock, reduces machine stock, and is just as
    /// costing-neutral as a refill, because the business still owns the units either way. A
    /// cost-bearing disposal is <em>not</em> this movement: discarding units follows the existing
    /// Damaged/Expired write-off semantics, which do consume costing inventory.
    /// </summary>
    ReturnToStorage = 1,

    /// <summary>
    /// One completed Nayax sale (status 12): the machine dispensed one unit, so machine stock falls
    /// by exactly one. It is the only kind that consumes business-owned costing inventory, and the
    /// ledger never performs that consumption itself - the sale-costing path does it exactly once
    /// (see <see cref="Inventory.Domain.Costing.WeightedAverageCostReplay"/>). The ledger reads the
    /// same completed-sale fact rather than recording a second movement for it, so there is nothing
    /// to double count.
    /// </summary>
    CompletedSale = 2,

    /// <summary>
    /// An operator's physical count of what is actually in the machine. It is an observation, not a
    /// transfer: it sets the expected level to the counted quantity from that instant on, and the
    /// difference it absorbs is reported as an unexplained change rather than written away as a
    /// correction. It changes no storage quantity, no costing quantity, no inventory value and no
    /// COGS - a count of machine stock is not a count of storage stock (compare
    /// <see cref="Inventory.Domain.InventoryCounting.InventoryCountAdjustmentPolicy"/>, which
    /// counts storage and does create a Restock/Correction movement).
    /// </summary>
    CountedReconciliation = 3
}

/// <summary>
/// A persisted <see cref="StockAdjustmentReason.MachineRefill"/> movement for one machine, as the
/// ledger reads it. <see cref="StorageQuantityChange"/> is signed exactly as
/// <c>StockAdjustment.QuantityChange</c> is - from storage's point of view - so a refill into the
/// machine is negative and a return to storage is positive.
/// </summary>
public sealed record MachineRefillFact(
    int StockAdjustmentId, long ProductId, int StorageQuantityChange, DateTime EffectiveAtUtc);

/// <summary>
/// One completed sale dispensed by the machine, identified by its Nayax transaction id. The product
/// is the one the sale was attributed to by <see cref="Inventory.Domain.Reporting.ProductMatching.ProductMatcher"/>,
/// the same attribution the costing ledger uses, so a sale cannot count against one product here
/// and another one there.
/// </summary>
public sealed record MachineSaleFact(long TransactionId, long ProductId, DateTime AuthorizationTimeUtc);

/// <summary>An operator's recorded physical count of one product in one machine.</summary>
public sealed record MachineStockCountFact(
    int CountId, long ProductId, int CountedQuantity, DateTime CountedAtUtc);

/// <summary>
/// The opening machine stock recorded for one product in one machine by the inventory-cost
/// transition (issue #298's <c>InventoryCostTransitionMachineStock</c>), at that transition's
/// cutoff. It is a persisted, explicitly applied baseline with its own provenance, which is what
/// makes it usable here; nothing is inferred from it beyond the quantity it records, and only
/// movements strictly after <see cref="CutoffAtUtc"/> are replayed on top of it.
/// </summary>
public sealed record MachineStockOpeningBaselineFact(
    long ProductId, int StockQuantity, DateTime CutoffAtUtc);

/// <summary>
/// One normalised movement in the ledger: what it did to the machine's units
/// (<see cref="MachineQuantityChange"/>), or - for a
/// <see cref="MachineStockMovementKind.CountedReconciliation"/> - the level it observed
/// (<see cref="CountedQuantity"/>). Exactly one of the two carries the movement's meaning, which is
/// why both exist on one record: a count does not describe a change at all.
/// </summary>
public sealed record MachineStockMovement(
    MachineStockMovementKind Kind,
    long ProductId,
    DateTime EffectiveAtUtc,
    long SourceId,
    int MachineQuantityChange,
    int? CountedQuantity);

/// <summary>
/// The one place a persisted fact becomes a machine-stock movement (issue #56), and the one place
/// the costing effect of each kind is written down.
///
/// Reading the refill sign here, once, is what keeps the ledger consistent with the storage history
/// it is derived from: <c>StockAdjustment.QuantityChange</c> is signed from storage's point of view,
/// so the machine's change is its negation, and the kind follows from the direction rather than from
/// a second persisted flag that could disagree with the quantity.
/// </summary>
public static class MachineStockMovementPolicy
{
    /// <summary>
    /// Whether this kind consumes business-owned costing inventory (<c>CostingQuantity</c>,
    /// <c>InventoryValue</c>, COGS). Only a completed sale does. A refill and a return to storage
    /// are internal transfers between two locations the business already owns, and a count is an
    /// observation.
    ///
    /// This is a statement about the movement's meaning, not an instruction to this ledger: the
    /// machine-stock ledger writes no cost at all. The sale-costing path remains the only writer of
    /// <c>UnitCostAtSale</c>/<c>CostOfGoodsSold</c>, and it records them once per completed sale.
    /// </summary>
    public static bool ConsumesBusinessCostingInventory(MachineStockMovementKind kind) =>
        kind == MachineStockMovementKind.CompletedSale;

    /// <summary>
    /// The movement a persisted machine-refill adjustment represents: a transfer into the machine
    /// when storage went down, and the reverse transfer when storage went up. A zero-quantity
    /// adjustment is a refill of nothing, which is still a real audit row and is kept as such rather
    /// than silently dropped.
    /// </summary>
    public static MachineStockMovement FromRefill(MachineRefillFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var machineQuantityChange = -fact.StorageQuantityChange;
        return new MachineStockMovement(
            machineQuantityChange >= 0 ? MachineStockMovementKind.Refill : MachineStockMovementKind.ReturnToStorage,
            fact.ProductId,
            fact.EffectiveAtUtc,
            fact.StockAdjustmentId,
            machineQuantityChange,
            CountedQuantity: null);
    }

    /// <summary>
    /// The movement one completed sale represents: exactly one unit out of the machine. One
    /// completed transaction dispenses one unit, which is the same quantity the weighted-average
    /// cost replay consumes from costing inventory for that sale, so the physical ledger and the
    /// costing ledger cannot drift apart on quantity.
    /// </summary>
    public static MachineStockMovement FromSale(MachineSaleFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return new MachineStockMovement(
            MachineStockMovementKind.CompletedSale,
            fact.ProductId,
            fact.AuthorizationTimeUtc,
            fact.TransactionId,
            MachineQuantityChange: -1,
            CountedQuantity: null);
    }

    /// <summary>
    /// The movement one recorded count represents. It carries no change at all - the level it
    /// observed is what the ledger continues from.
    /// </summary>
    public static MachineStockMovement FromCount(MachineStockCountFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return new MachineStockMovement(
            MachineStockMovementKind.CountedReconciliation,
            fact.ProductId,
            fact.CountedAtUtc,
            fact.CountId,
            MachineQuantityChange: 0,
            fact.CountedQuantity);
    }
}
