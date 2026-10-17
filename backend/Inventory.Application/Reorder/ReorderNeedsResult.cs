namespace Inventory.Application.Reorder;

/// <summary>
/// One machine selection exactly as the live fleet read returned it: one mapping of a Nayax
/// catalogue product onto one machine, carrying the PAR, missing-stock and vend-out-alert-threshold
/// fields of the <c>machineProducts</c> contract. <see cref="MachineId"/> is the machine the request
/// was made for, not the payload's own nullable <c>MachineID</c> field, so a selection can always be
/// attributed to a machine.
///
/// This is the raw read, with no catalogue knowledge attached: whether the product is known locally
/// and active is resolved by the caller, which is what turns these into the Domain
/// <see cref="Inventory.Domain.Machines.MachineSelectionStockFact"/> the refill rules take.
/// </summary>
public readonly record struct MachineSelectionStock(
    long MachineId,
    long? ProductId,
    int Par,
    int MissingStockByMdb,
    int VendOutAlertThreshold);

/// <summary>
/// The per-product facts <see cref="CalculateReorderNeeds"/> aggregates across the machine fleet and
/// outstanding supplier orders (issue #47). Keyed by the catalogue's own product ID (the same ID
/// Nayax reports back as <c>NayaxProductID</c>) so a caller can look a product up without this use
/// case needing to know which products a particular search/category/supplier filter selected.
///
/// <para>Since issue #726 every result also carries the <see cref="Mode"/> it was produced in, and
/// the reason the machines' need is missing when it was not accounted for. A caller must publish
/// that rather than read an absent machine need as "the machines need nothing": the two are
/// different answers, and only one of them is a fact about the fleet.</para>
/// </summary>
public sealed record ReorderNeedsResult(
    IReadOnlyDictionary<long, int> MachineReplenishmentNeedByProductId,
    IReadOnlyDictionary<long, decimal> OnOrderQuantityByProductId)
{
    /// <summary>
    /// Which facts this result accounted for. <see langword="required"/> so no result exists without
    /// one: "every result carries its mode" is the contract, not a convention.
    /// </summary>
    public required ReorderMode Mode { get; init; }

    /// <summary>
    /// Why the machines' replenishment need is not in this result, for a
    /// <see cref="ReorderMode.StorageOnly"/> one, and <see langword="null"/> for a
    /// <see cref="ReorderMode.MachineAware"/> one - where there is nothing unavailable to explain.
    /// </summary>
    public MachineNeedUnavailableReason? MachineNeedUnavailableReason { get; init; }

    /// <summary>
    /// The storage-only result (issue #726): outstanding supplier-order quantity, no machine need at
    /// all, and the reason the fleet could not be accounted for.
    ///
    /// <para>It is a factory rather than an object initializer so a storage-only result cannot be
    /// built holding a machine need, a machine id or a machine selection - the empty collections are
    /// not a convention a caller follows but the only shape this mode has - and so the reason is not
    /// optional in the one mode that needs it.</para>
    /// </summary>
    public static ReorderNeedsResult StorageOnly(
        IReadOnlyDictionary<long, decimal> onOrderQuantityByProductId,
        MachineNeedUnavailableReason reason) =>
        new(new Dictionary<long, int>(), onOrderQuantityByProductId)
        {
            Mode = ReorderMode.StorageOnly,
            MachineNeedUnavailableReason = reason,
        };

    /// <summary>
    /// Every machine the live fleet read covered, in ascending id order, including machines that
    /// reported no selections at all. A count of zero is how a caller tells "nothing needs attention"
    /// from "there was nothing to look at" (issue #459).
    /// </summary>
    public IReadOnlyList<long> MachineIds { get; init; } = [];

    /// <summary>
    /// The individual machine selections the same fleet read returned, which the per-product
    /// replenishment totals above are summed from. Exposed additively (issue #459) so the home
    /// Dashboard's refill card can apply the Domain refill rules to the machines' current stock
    /// without a second <c>GetMachineProductsAsync</c> fan-out across the fleet. Existing callers
    /// that only need the per-product totals ignore it.
    /// </summary>
    public IReadOnlyList<MachineSelectionStock> MachineSelections { get; init; } = [];
}
