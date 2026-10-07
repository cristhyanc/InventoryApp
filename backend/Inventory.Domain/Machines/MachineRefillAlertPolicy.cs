namespace Inventory.Domain.Machines;

/// <summary>
/// One machine selection's stock facts - one mapping of a catalogue product onto one machine, as the
/// Nayax <c>machineProducts</c> contract reports it. <see cref="Par"/> is the standard quantity that
/// should be stocked, <see cref="MissingStockByMdb"/> the quantity missing according to MDB data, and
/// <see cref="VendOutAlertThreshold"/> the stock level at which Nayax raises a vend-out alert (all
/// three confirmed against the published <c>GET /machines/{id}/machineProducts</c> contract, issue
/// #459); the current quantity is <c>Par - MissingStockByMdb</c>, the same arithmetic the Pick List
/// and the site dashboard already use. <see cref="ProductId"/>/<see cref="ProductIsActive"/> are
/// resolved by the caller from the local catalogue before this fact is built, exactly as
/// <see cref="Inventory.Domain.Sites.SiteMachineProductStockFact"/> requires.
/// </summary>
public sealed record MachineSelectionStockFact(
    long MachineId,
    long? ProductId,
    bool ProductIsActive,
    int Par,
    int MissingStockByMdb,
    int VendOutAlertThreshold);

/// <summary>
/// Whether one selection is adequately stocked, low, or empty. The three states are mutually
/// exclusive: a selection at or below zero is <see cref="Empty"/> and is never also counted as
/// <see cref="Low"/>.
/// </summary>
public enum MachineSelectionStockLevel
{
    Stocked = 0,
    Low = 1,
    Empty = 2,
}

/// <summary>
/// The refill alert counts for a machine fleet. The selection counts and the machine counts answer
/// different questions and must not be added together:
/// <list type="bullet">
/// <item><see cref="LowSelectionCount"/>/<see cref="EmptySelectionCount"/> count selections, and are
/// disjoint.</item>
/// <item><see cref="MachinesWithLowSelections"/>/<see cref="MachinesWithEmptySelections"/> count
/// machines and do overlap: a machine carrying both a low and an empty selection appears in
/// both.</item>
/// <item><see cref="MachinesNeedingRefill"/> is the distinct machines with at least one low or empty
/// selection, so it counts such a machine exactly once and is never the sum of the two machine
/// counts above.</item>
/// </list>
/// </summary>
public readonly record struct MachineRefillAlertSummary(
    int MachinesNeedingRefill,
    int MachinesWithEmptySelections,
    int MachinesWithLowSelections,
    int EmptySelectionCount,
    int LowSelectionCount,
    int SelectionsEvaluated);

/// <summary>
/// The deterministic low/empty refill rules for a machine fleet (issue #459). The per-selection
/// low/empty test in <see cref="Classify"/> is the one authoritative implementation: the site
/// dashboard's own product alert counts (<see cref="Inventory.Domain.Sites.SiteStockPolicy"/>) call
/// it too, so "low" and "empty" cannot come to mean different things on the home Dashboard and on a
/// site row.
///
/// This policy answers the fleet question the site policy cannot: how many *machines* need a refill.
/// Aggregating the site counts would double count, because a product that is low on two machines at
/// one site is one site-level alert and two machine-level ones, and a machine serving two sites
/// would be counted once per site.
/// </summary>
public static class MachineRefillAlertPolicy
{
    /// <summary>
    /// One selection's stock level. Empty wins over low, so the two counts stay disjoint; the low
    /// comparison is inclusive, matching the existing site alert rule exactly (a selection sitting
    /// on its vend-out alert threshold is already low).
    /// </summary>
    public static MachineSelectionStockLevel Classify(int quantity, int vendOutAlertThreshold) =>
        quantity <= 0
            ? MachineSelectionStockLevel.Empty
            : quantity <= vendOutAlertThreshold
                ? MachineSelectionStockLevel.Low
                : MachineSelectionStockLevel.Stocked;

    /// <summary>
    /// Summarizes the fleet's selections. A selection with no mapped catalogue product, or one whose
    /// product is inactive, is not evaluated at all - the same exclusion the site alert counts
    /// already apply, so an unmapped MDB slot cannot invent a refill alert. Several mappings of one
    /// product on one machine are summed into a single selection first, the aggregation the Pick List
    /// already applies to duplicate MDB slots.
    /// </summary>
    public static MachineRefillAlertSummary Summarize(IEnumerable<MachineSelectionStockFact> selections)
    {
        var evaluated = selections
            .Where(selection => selection.ProductId.HasValue && selection.ProductIsActive)
            .GroupBy(selection => (selection.MachineId, ProductId: selection.ProductId!.Value))
            .Select(slots => (
                slots.Key.MachineId,
                Level: Classify(
                    slots.Sum(slot => slot.Par - slot.MissingStockByMdb),
                    slots.Sum(slot => slot.VendOutAlertThreshold))))
            .ToList();

        var alerting = evaluated
            .Where(selection => selection.Level != MachineSelectionStockLevel.Stocked)
            .ToList();

        return new MachineRefillAlertSummary(
            MachinesNeedingRefill: DistinctMachines(alerting),
            MachinesWithEmptySelections: DistinctMachines(At(alerting, MachineSelectionStockLevel.Empty)),
            MachinesWithLowSelections: DistinctMachines(At(alerting, MachineSelectionStockLevel.Low)),
            EmptySelectionCount: At(alerting, MachineSelectionStockLevel.Empty).Count,
            LowSelectionCount: At(alerting, MachineSelectionStockLevel.Low).Count,
            SelectionsEvaluated: evaluated.Count);
    }

    private static List<(long MachineId, MachineSelectionStockLevel Level)> At(
        List<(long MachineId, MachineSelectionStockLevel Level)> selections, MachineSelectionStockLevel level) =>
        selections.Where(selection => selection.Level == level).ToList();

    private static int DistinctMachines(List<(long MachineId, MachineSelectionStockLevel Level)> selections) =>
        selections.Select(selection => selection.MachineId).Distinct().Count();
}
