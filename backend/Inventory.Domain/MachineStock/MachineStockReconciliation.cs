namespace Inventory.Domain.MachineStock;

/// <summary>
/// One machine slot's reported stock facts for a product, as the machine-stock source publishes
/// them. <see cref="Par"/> is the standard quantity that should be stocked and
/// <see cref="MissingStockByMdb"/> the quantity missing according to MDB data (both confirmed
/// against the published Nayax <c>GET /v1/machines/{MachineID}/machineProducts</c> contract, which
/// documents each as a nullable <c>int32</c>); the observed quantity is
/// <c>PAR - MissingStockByMDB</c>, the same arithmetic the Pick List, the site dashboard and the
/// inventory-cost transition already use.
/// </summary>
public sealed record MachineStockObservationSlot(long ProductId, int? Par, int? MissingStockByMdb);

/// <summary>
/// How the machine's own reported stock becomes an observed quantity (issue #56), and when it
/// becomes no quantity at all.
///
/// It is deliberately separate from
/// <see cref="Inventory.Domain.Costing.InventoryCostTransitionPolicy.MachineQuantity"/>, which
/// performs the same arithmetic and <em>refuses</em> incomplete data: a one-time costing cutover
/// must be exact or not happen, while a reconciliation report has to stay available and say which
/// products it could not confirm. The arithmetic is the same; what differs is the answer to
/// incomplete source data, and each of the two states its own answer.
/// </summary>
public static class MachineStockObservationPolicy
{
    /// <summary>
    /// The product's observed quantity in one machine: the sum over its slots of
    /// <c>PAR - MissingStockByMDB</c>. The answer is <c>null</c> - unknown, never zero - when the
    /// product occupies no slot, when any slot is missing either value, or when any slot's quantity
    /// falls outside <c>0..PAR</c> and so is not a plausible physical count. One unusable slot makes
    /// the whole product unobserved, because a partial sum of a multi-slot product is not the
    /// quantity in the machine.
    /// </summary>
    public static int? ObservedQuantity(IEnumerable<MachineStockObservationSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        var quantity = 0;
        var observed = false;
        foreach (var slot in slots)
        {
            if (slot.Par is not { } par || slot.MissingStockByMdb is not { } missing)
                return null;

            var slotQuantity = par - missing;
            if (slotQuantity < 0 || slotQuantity > par)
                return null;

            quantity = checked(quantity + slotQuantity);
            observed = true;
        }

        return observed ? quantity : null;
    }
}

/// <summary>How a product's observed machine stock compares with its expected machine stock.</summary>
public enum MachineStockReconciliationStatus
{
    /// <summary>Expected or observed stock is unknown, so no comparison is possible.</summary>
    Unknown = 0,

    /// <summary>Observed equals expected.</summary>
    Balanced = 1,

    /// <summary>Fewer units are in the machine than the ledger expects: unexplained loss.</summary>
    Shortage = 2,

    /// <summary>More units are in the machine than the ledger expects: an unrecorded refill or an over-fill.</summary>
    Overage = 3
}

/// <summary>
/// One product's reconciliation on one machine: its projected ledger position, the observed
/// quantity, and the difference between them. <see cref="DiscrepancyQuantity"/> is
/// <c>observed - expected</c>; <see cref="ShrinkageQuantity"/> and <see cref="OverageQuantity"/> are
/// the one-sided views of the same number, so a caller never has to re-derive a sign. All four are
/// <c>null</c> whenever either side is unknown.
/// </summary>
public sealed record MachineStockProductReconciliation(
    long ProductId,
    int? ExpectedQuantity,
    int? ObservedQuantity,
    int? DiscrepancyQuantity,
    int? ShrinkageQuantity,
    int? OverageQuantity,
    MachineStockReconciliationStatus Status,
    MachineStockBaselineSource BaselineSource,
    DateTime? BaselineAtUtc,
    int? BaselineQuantity,
    int RefilledQuantity,
    int ReturnedToStorageQuantity,
    int SoldQuantity,
    int? UnexplainedChangeAtLastCount,
    IReadOnlyList<MachineStockDataQualityIssue> Issues);

/// <summary>
/// The comparison of expected against observed machine stock (issue #56). It invents nothing: an
/// unknown on either side produces an unknown discrepancy and the
/// <see cref="MachineStockDataQualityIssueCodes.AwaitingOpeningCount"/>/
/// <see cref="MachineStockDataQualityIssueCodes.ObservedStockUnavailable"/> state that explains why,
/// and it never proposes or applies a correction - identifying shrinkage is the whole point, so
/// writing it away would defeat it.
/// </summary>
public static class MachineStockReconciliationPolicy
{
    /// <summary>
    /// Reconciles one machine: every product the ledger projected and every product the machine
    /// reported a slot for, ordered by product id. A product the machine reports but the ledger has
    /// never seen is included with no expected stock
    /// (<see cref="MachineStockLedger.NoRecordedHistory"/>), because an observed quantity is worth
    /// reporting even before the ledger can be compared against it; a product the ledger knows but
    /// the machine does not report keeps its expected stock with an unavailable observation.
    /// </summary>
    public static IReadOnlyList<MachineStockProductReconciliation> ReconcileMachine(
        IReadOnlyCollection<MachineStockProductLedger> ledger,
        IReadOnlyCollection<MachineStockObservationSlot> observedSlots)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(observedSlots);

        var observedByProduct = observedSlots
            .GroupBy(slot => slot.ProductId)
            .ToDictionary(group => group.Key, ObservedQuantityOf);
        var ledgerByProduct = ledger.ToDictionary(product => product.ProductId);

        return ledgerByProduct.Keys
            .Concat(observedByProduct.Keys)
            .Distinct()
            .OrderBy(productId => productId)
            .Select(productId => Reconcile(
                ledgerByProduct.GetValueOrDefault(productId) ?? MachineStockLedger.NoRecordedHistory(productId),
                observedByProduct.TryGetValue(productId, out var observed) ? observed : null))
            .ToList();
    }

    private static int? ObservedQuantityOf(IEnumerable<MachineStockObservationSlot> slots) =>
        MachineStockObservationPolicy.ObservedQuantity(slots);

    /// <summary>
    /// Reconciles one projected product position against the quantity observed for it.
    /// <paramref name="observedQuantity"/> is the already-resolved observation
    /// (<see cref="MachineStockObservationPolicy.ObservedQuantity"/>), or <c>null</c> when the source
    /// could not report one.
    /// </summary>
    public static MachineStockProductReconciliation Reconcile(
        MachineStockProductLedger ledger, int? observedQuantity)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var issues = ledger.Issues.ToList();
        if (observedQuantity is null)
        {
            issues.Add(new MachineStockDataQualityIssue(
                MachineStockDataQualityIssueCodes.ObservedStockUnavailable,
                $"The machine reported no usable stock quantity for product {ledger.ProductId}, so its "
                    + "expected stock cannot be confirmed and no discrepancy is reported."));
        }

        int? discrepancy = ledger.ExpectedQuantity is { } expected && observedQuantity is { } observed
            ? observed - expected
            : null;
        var status = discrepancy switch
        {
            null => MachineStockReconciliationStatus.Unknown,
            0 => MachineStockReconciliationStatus.Balanced,
            < 0 => MachineStockReconciliationStatus.Shortage,
            _ => MachineStockReconciliationStatus.Overage
        };
        int? shrinkageQuantity = discrepancy switch
        {
            null => null,
            < 0 => -discrepancy.Value,
            _ => 0
        };
        int? overageQuantity = discrepancy switch
        {
            null => null,
            > 0 => discrepancy.Value,
            _ => 0
        };

        return new MachineStockProductReconciliation(
            ledger.ProductId,
            ledger.ExpectedQuantity,
            observedQuantity,
            discrepancy,
            shrinkageQuantity,
            overageQuantity,
            status,
            ledger.BaselineSource,
            ledger.BaselineAtUtc,
            ledger.BaselineQuantity,
            ledger.RefilledQuantity,
            ledger.ReturnedToStorageQuantity,
            ledger.SoldQuantity,
            ledger.UnexplainedChangeAtLastCount,
            issues);
    }
}
