using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Costing;

/// <summary>One Nayax machine slot's reported stock facts, as the transition reads them.</summary>
public sealed record InventoryCostTransitionSlot(int? Par, int? MissingStockByMdb);

/// <summary>The opening costing position a transition preview derives for one product.</summary>
public sealed record InventoryCostTransitionOpening(
    int MachineStockQuantity,
    int OpeningCostingQuantity,
    decimal InventoryValue,
    int LegacyPhysicalDiscrepancy,
    string DataQualityNote);

/// <summary>One machine's stock quantity for a product at the transition cutoff.</summary>
public sealed record InventoryCostTransitionMachineQuantity(long MachineId, int StockQuantity);

/// <summary>
/// The facts of one product's transition preview that stale-preview validation compares: the
/// previewed values and the inputs they were derived from.
/// </summary>
public sealed record InventoryCostTransitionState(
    long ProductId,
    string ProductName,
    int HomeStockQuantity,
    IReadOnlyList<InventoryCostTransitionMachineQuantity> MachineStocks,
    int MachineStockQuantity,
    int OpeningCostingQuantity,
    decimal AverageUnitCost,
    decimal InventoryValue,
    int LegacyReplayedPhysicalQuantity);

/// <summary>
/// The deterministic rules of the inventory-cost transition (issue #298, child 4 of #149), moved
/// unchanged from the former <c>InventoryApi.Services.InventoryCostTransitionService</c>: a
/// machine's stock for a product is the sum over its slots of <c>PAR - MissingStockByMDB</c>; the
/// opening costing quantity is verified home stock plus every machine's stock, valued at the
/// opening average unit cost; the legacy physical-movement replay is never altered, only its
/// discrepancy against verified home stock recorded; and a preview may be applied once, before it
/// expires, and only while the inventory it was built from is unchanged. Every check throws a
/// caller-safe <see cref="DomainValidationException"/> with the message the API has always returned.
/// </summary>
public static class InventoryCostTransitionPolicy
{
    /// <summary>The provenance recorded for every machine's transition stock quantity.</summary>
    public const string MachineStockSource = "Nayax PAR - MissingStockByMDB";

    /// <summary>How long a stored preview may be applied after it was created.</summary>
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    public static void EnsureValidOpeningCost(decimal averageUnitCost)
    {
        if (averageUnitCost < 0)
            throw new DomainValidationException("The opening average unit cost cannot be negative.");
    }

    /// <summary>
    /// The product's stock in one machine: the sum over its slots of <c>PAR - MissingStockByMDB</c>.
    /// A slot missing either value, or reporting a quantity outside <c>0..PAR</c>, is rejected rather
    /// than guessed.
    /// </summary>
    /// <exception cref="OverflowException">The machine's quantity is outside <see cref="int"/> range.</exception>
    public static int MachineQuantity(
        long productId,
        string machineLabel,
        IEnumerable<InventoryCostTransitionSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        var quantity = 0;
        foreach (var slot in slots)
        {
            if (!slot.Par.HasValue || !slot.MissingStockByMdb.HasValue)
                throw new DomainValidationException(
                    $"Nayax stock is incomplete for product {productId} in machine {machineLabel}: PAR and MissingStockByMDB are required.");
            var slotQuantity = slot.Par.Value - slot.MissingStockByMdb.Value;
            if (slotQuantity < 0 || slotQuantity > slot.Par.Value)
                throw new DomainValidationException(
                    $"Nayax stock is invalid for product {productId} in machine {machineLabel}.");
            quantity = checked(quantity + slotQuantity);
        }

        return quantity;
    }

    /// <summary>
    /// The opening costing position: home stock plus every machine's stock, valued at
    /// <paramref name="averageUnitCost"/>, with the legacy physical replay's discrepancy against
    /// verified home stock recorded as a data-quality note rather than corrected.
    /// </summary>
    /// <exception cref="OverflowException">A quantity is outside <see cref="int"/> range.</exception>
    public static InventoryCostTransitionOpening CalculateOpening(
        int homeStockQuantity,
        IEnumerable<int> machineStockQuantities,
        decimal averageUnitCost,
        int legacyReplayedPhysicalQuantity)
    {
        ArgumentNullException.ThrowIfNull(machineStockQuantities);

        var machineQuantity = machineStockQuantities.Sum();
        var costingQuantity = checked(homeStockQuantity + machineQuantity);
        var discrepancy = homeStockQuantity - legacyReplayedPhysicalQuantity;
        var note = discrepancy == 0
            ? "Legacy physical movement history reconciled at transition."
            : $"Legacy physical movement history replayed to {legacyReplayedPhysicalQuantity}, while verified home stock was {homeStockQuantity}; discrepancy {discrepancy:+#;-#;0} was retired at cutover without altering legacy movements.";
        return new(machineQuantity, costingQuantity, costingQuantity * averageUnitCost, discrepancy, note);
    }

    /// <summary>A stored preview may be applied only once and only before it expires.</summary>
    public static void EnsureDraftUsable(DateTime? appliedAt, DateTime expiresAt, DateTime now)
    {
        if (appliedAt.HasValue)
            throw new DomainValidationException("This transition preview has already been applied.");
        if (expiresAt < now)
            throw new DomainValidationException("The transition preview expired. Run the preview again.");
    }

    /// <summary>
    /// Rejects a stale preview: the home stock, legacy replay and every machine's stock must still
    /// match <paramref name="current"/>, and the previewed values must still follow from their inputs.
    /// </summary>
    public static void EnsureUnchanged(InventoryCostTransitionState expected, InventoryCostTransitionState current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);

        var expectedMachines = expected.MachineStocks.OrderBy(x => x.MachineId).ToArray();
        var currentMachines = current.MachineStocks.OrderBy(x => x.MachineId).ToArray();
        var machineStocksMatch = expectedMachines.Length == currentMachines.Length &&
            expectedMachines.Zip(currentMachines).All(pair =>
                pair.First.MachineId == pair.Second.MachineId &&
                pair.First.StockQuantity == pair.Second.StockQuantity);
        var expectedMachineQuantity = expectedMachines.Sum(x => x.StockQuantity);

        if (expected.HomeStockQuantity != current.HomeStockQuantity ||
            expected.LegacyReplayedPhysicalQuantity != current.LegacyReplayedPhysicalQuantity ||
            !machineStocksMatch)
            throw new DomainValidationException(
                "Inventory data changed after the preview. Run the preview again before confirming.");
        if (expected.MachineStockQuantity != expectedMachineQuantity ||
            expected.OpeningCostingQuantity != expected.HomeStockQuantity + expected.MachineStockQuantity ||
            expected.InventoryValue != expected.OpeningCostingQuantity * expected.AverageUnitCost)
            throw new DomainValidationException("The confirmed transition values do not match the preview calculation.");
    }

    /// <summary>
    /// Rejects a stale all-products preview: the eligible products and each product's average unit
    /// cost must be unchanged, and each product must pass <see cref="EnsureUnchanged"/>.
    /// </summary>
    public static void EnsureBatchUnchanged(
        IReadOnlyCollection<InventoryCostTransitionState> expected,
        IReadOnlyCollection<InventoryCostTransitionState> current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);

        if (expected.Count != current.Count)
            throw new DomainValidationException("The eligible product list changed after the preview. Run it again.");
        var currentByProduct = current.ToDictionary(x => x.ProductId);
        foreach (var product in expected)
        {
            if (!currentByProduct.TryGetValue(product.ProductId, out var currentProduct))
                throw new DomainValidationException("The eligible product list changed after the preview. Run it again.");
            if (product.AverageUnitCost != currentProduct.AverageUnitCost)
                throw new DomainValidationException(
                    $"The average unit cost for {product.ProductName} changed after the preview. Run it again.");
            EnsureUnchanged(product, currentProduct);
        }
    }
}
