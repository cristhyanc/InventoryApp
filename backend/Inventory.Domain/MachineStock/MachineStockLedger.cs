using Inventory.Domain.Products;

namespace Inventory.Domain.MachineStock;

/// <summary>Where the expected machine stock of one product is counted from.</summary>
public enum MachineStockBaselineSource
{
    /// <summary>
    /// Nothing authoritative says how many units were in the machine to begin with, so expected
    /// stock is unknown and stays unknown. The refills and sales since then are still real facts and
    /// are reported as such; what cannot be derived from them is a physical level.
    /// </summary>
    None = 0,

    /// <summary>An operator's physical count of this product in this machine: the strongest baseline.</summary>
    CountedReconciliation = 1,

    /// <summary>
    /// The opening machine stock the inventory-cost transition recorded for this product in this
    /// machine at its cutoff (issue #298), used only while no later count exists.
    /// </summary>
    CostTransitionBaseline = 2
}

/// <summary>A data-quality problem found while projecting or reconciling machine stock.</summary>
public sealed record MachineStockDataQualityIssue(string Code, string Message);

/// <summary>
/// The data-quality states the machine-stock ledger reports (issue #56). Every one of them exists so
/// an uncertain reconciliation can be published as uncertain: none of them is ever resolved by
/// guessing a quantity, and none of them is written back as a correction.
/// </summary>
public static class MachineStockDataQualityIssueCodes
{
    /// <summary>
    /// No count and no transition baseline, so the product's expected machine stock is unknown.
    /// Refilled, returned and sold quantities are still reported; expected stock, the discrepancy
    /// and shrinkage are <c>null</c>.
    /// </summary>
    public const string AwaitingOpeningCount = "AwaitingOpeningCount";

    /// <summary>
    /// The source could not say how many units the machine is holding for this product: the machine
    /// reports no slot for it, or a slot is missing <c>PAR</c>/<c>MissingStockByMDB</c>, or a slot's
    /// numbers are not a plausible quantity. The discrepancy and shrinkage are <c>null</c>.
    /// </summary>
    public const string ObservedStockUnavailable = "ObservedStockUnavailable";

    /// <summary>
    /// The replayed expected stock went below zero, which a physical quantity cannot do: the ledger
    /// is missing a refill, or a sale has been attributed to a machine that did not dispense it. The
    /// negative value is reported rather than clamped, because clamping would hide the gap.
    /// </summary>
    public const string ExpectedStockNegative = "ExpectedStockNegative";

    /// <summary>
    /// Machine-level: the machine's own stock source could not be read at all (Nayax is not
    /// connected for this business, or has not granted permission for it), so nothing observed is
    /// available to reconcile against. Expected stock is still reported.
    /// </summary>
    public const string ObservationSourceUnavailable = "ObservationSourceUnavailable";

    /// <summary>
    /// Machine-level: Nayax stock-adjustment events for this machine are still unprocessed in Sync
    /// Restock (issue #183). Refills this machine has already received may therefore be missing from
    /// the ledger, so a shortage shown here may be an unapplied refill rather than shrinkage.
    /// </summary>
    public const string PendingNayaxStockEvents = "PendingNayaxStockEvents";

    /// <summary>
    /// Machine-level: completed sales on this machine could not be attributed to a catalogue
    /// product, so the units they dispensed are missing from every product's expected stock.
    /// </summary>
    public const string UnattributedCompletedSales = "UnattributedCompletedSales";
}

/// <summary>The facts one machine's ledger is projected from.</summary>
public sealed record MachineStockLedgerInputs(
    IReadOnlyCollection<MachineRefillFact> Refills,
    IReadOnlyCollection<MachineSaleFact> Sales,
    IReadOnlyCollection<MachineStockCountFact> Counts,
    IReadOnlyCollection<MachineStockOpeningBaselineFact> OpeningBaselines);

/// <summary>
/// One product's projected machine-stock position. <see cref="ExpectedQuantity"/> is <c>null</c>
/// exactly when no baseline exists; the movement totals are counted from
/// <see cref="BaselineAtUtc"/> when there is one, and over the whole history when there is not.
/// <see cref="UnexplainedChangeAtLastCount"/> is the difference the most recent count absorbed -
/// positive means units were missing - and is <c>null</c> when no count has been recorded or when
/// nothing was expected to compare it against. <see cref="ProductName"/> (issue #834) is whatever the
/// caller passed to <see cref="MachineStockLedger.Project"/>, used only to name the product in this
/// product's data-quality messages; it is <c>null</c> when the caller did not supply one.
/// </summary>
public sealed record MachineStockProductLedger(
    long ProductId,
    int? ExpectedQuantity,
    MachineStockBaselineSource BaselineSource,
    DateTime? BaselineAtUtc,
    int? BaselineQuantity,
    int RefilledQuantity,
    int ReturnedToStorageQuantity,
    int SoldQuantity,
    int? UnexplainedChangeAtLastCount,
    IReadOnlyList<MachineStockDataQualityIssue> Issues,
    string? ProductName = null);

/// <summary>
/// The machine-stock ledger projection (issue #56): the one authoritative derivation of what a
/// machine is expected to be holding.
///
/// It is a replay, in the same shape as
/// <see cref="Inventory.Domain.Costing.WeightedAverageCostReplay"/> and for the same reason: the
/// authoritative facts already exist - machine-refill stock movements, completed Nayax sales, and
/// the operator's counts - so the ledger reads them in order rather than writing a second movement
/// row per event. That is what makes double counting structurally impossible instead of merely
/// unlikely: there is nothing to keep in step, because there is only one record of each event.
///
/// Two rules carry the whole design:
/// <list type="number">
/// <item>Expected stock is never invented. Without a counted or transition baseline it is
/// <c>null</c> and the product is reported as awaiting an opening count.</item>
/// <item>A count supersedes rather than corrects. It sets the level from its own instant onward and
/// reports the difference it absorbed; it never changes storage stock, costing inventory or COGS,
/// and it never rewrites the movements before it.</item>
/// </list>
///
/// Ordering is by effective instant, then by kind - refills, then returns, then sales, then the
/// count - then by source id. A count ordering last at an instant is deliberate: a count is the
/// authority on what is physically there at that moment, so everything else at that instant is
/// replayed before it.
///
/// The instants come from different sources (a movement's <c>EffectiveAt</c>, a sale's
/// authorization instant, a count's recorded instant), exactly as the costing replay's do, and are
/// compared as UTC instants; the known sale-timestamp caveat recorded in <c>docs/architecture.md</c>
/// § Nayax sale timestamps applies here unchanged and is not re-solved by this ledger.
/// </summary>
public static class MachineStockLedger
{
    private const int RefillPriority = 0;
    private const int ReturnPriority = 1;
    private const int SalePriority = 2;
    private const int CountPriority = 3;

    /// <summary>
    /// Projects every product the facts mention, ordered by product id. A product with a baseline
    /// but no movement is included (its expected stock is its baseline); a product with movements
    /// but no baseline is included too, with an unknown expected stock.
    /// </summary>
    public static IReadOnlyList<MachineStockProductLedger> Project(
        MachineStockLedgerInputs inputs, IReadOnlyDictionary<long, string>? productNames = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var movements = inputs.Refills.Select(MachineStockMovementPolicy.FromRefill)
            .Concat(inputs.Sales.Select(MachineStockMovementPolicy.FromSale))
            .Concat(inputs.Counts.Select(MachineStockMovementPolicy.FromCount))
            .ToList();
        var baselinesByProduct = inputs.OpeningBaselines
            .GroupBy(baseline => baseline.ProductId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(baseline => baseline.CutoffAtUtc).First());

        return movements.Select(movement => movement.ProductId)
            .Concat(baselinesByProduct.Keys)
            .Distinct()
            .OrderBy(productId => productId)
            .Select(productId => ProjectProduct(
                productId,
                movements.Where(movement => movement.ProductId == productId),
                baselinesByProduct.GetValueOrDefault(productId),
                productNames?.GetValueOrDefault(productId)))
            .ToList();
    }

    /// <summary>
    /// The position of a product this machine's ledger holds nothing at all for: no baseline and no
    /// movement. It is the awaiting-an-opening-count state, built by the same projection rather than
    /// assembled by hand, so a product the machine reports but the ledger has never seen reads
    /// exactly like every other unbaselined product.
    /// </summary>
    public static MachineStockProductLedger NoRecordedHistory(long productId, string? productName = null) =>
        ProjectProduct(productId, [], openingBaseline: null, productName);

    private static MachineStockProductLedger ProjectProduct(
        long productId,
        IEnumerable<MachineStockMovement> productMovements,
        MachineStockOpeningBaselineFact? openingBaseline,
        string? productName = null)
    {
        var issues = new List<MachineStockDataQualityIssue>();
        int? expected = openingBaseline?.StockQuantity;
        var baselineSource = openingBaseline is null
            ? MachineStockBaselineSource.None
            : MachineStockBaselineSource.CostTransitionBaseline;
        var baselineAt = openingBaseline?.CutoffAtUtc;
        int? baselineQuantity = openingBaseline?.StockQuantity;
        var refilled = 0;
        var returned = 0;
        var sold = 0;
        int? unexplainedAtLastCount = null;
        var expectedWentNegative = false;

        var ordered = productMovements
            // Strictly after the cutoff, the same boundary the costing replay applies to its own
            // baseline: an event at the cutoff instant is part of the position the baseline records.
            .Where(movement => openingBaseline is null || movement.EffectiveAtUtc > openingBaseline.CutoffAtUtc)
            .OrderBy(ReplayOrder);

        foreach (var movement in ordered)
        {
            switch (movement.Kind)
            {
                case MachineStockMovementKind.CountedReconciliation:
                    var counted = movement.CountedQuantity!.Value;
                    unexplainedAtLastCount = expected.HasValue ? expected.Value - counted : null;
                    expected = counted;
                    baselineSource = MachineStockBaselineSource.CountedReconciliation;
                    baselineAt = movement.EffectiveAtUtc;
                    baselineQuantity = counted;
                    refilled = 0;
                    returned = 0;
                    sold = 0;
                    expectedWentNegative = false;
                    break;

                case MachineStockMovementKind.Refill:
                    refilled += movement.MachineQuantityChange;
                    expected += movement.MachineQuantityChange;
                    break;

                case MachineStockMovementKind.ReturnToStorage:
                    returned += -movement.MachineQuantityChange;
                    expected += movement.MachineQuantityChange;
                    break;

                default: // MachineStockMovementKind.CompletedSale
                    sold += -movement.MachineQuantityChange;
                    expected += movement.MachineQuantityChange;
                    break;
            }

            if (expected is < 0)
                expectedWentNegative = true;
        }

        if (baselineSource == MachineStockBaselineSource.None)
        {
            issues.Add(new MachineStockDataQualityIssue(
                MachineStockDataQualityIssueCodes.AwaitingOpeningCount,
                $"Expected machine stock for {ProductLabel.Phrase(productId, productName)} is unknown: no "
                    + "counted or transition baseline exists for this machine, so the refills and sales "
                    + "since then cannot be turned into a physical quantity. Record a count to start the "
                    + "ledger."));
        }

        if (expectedWentNegative)
        {
            issues.Add(new MachineStockDataQualityIssue(
                MachineStockDataQualityIssueCodes.ExpectedStockNegative,
                $"Expected machine stock for {ProductLabel.Phrase(productId, productName)} went below zero "
                    + "while replaying this machine's ledger, which a physical quantity cannot do: a "
                    + "refill is missing, or a completed sale is attributed to this machine in error."));
        }

        return new MachineStockProductLedger(
            productId,
            expected,
            baselineSource,
            baselineAt,
            baselineQuantity,
            refilled,
            returned,
            sold,
            unexplainedAtLastCount,
            issues,
            productName);
    }

    /// <summary>The one replay ordering key: effective instant, then movement kind, then source id.</summary>
    private static (DateTime EffectiveAtUtc, int Priority, long SourceId) ReplayOrder(MachineStockMovement movement) =>
        (movement.EffectiveAtUtc, Priority(movement.Kind), movement.SourceId);

    private static int Priority(MachineStockMovementKind kind) => kind switch
    {
        MachineStockMovementKind.Refill => RefillPriority,
        MachineStockMovementKind.ReturnToStorage => ReturnPriority,
        MachineStockMovementKind.CompletedSale => SalePriority,
        _ => CountPriority
    };
}
