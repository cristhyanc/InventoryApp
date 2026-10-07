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
/// </summary>
public sealed record ReorderNeedsResult(
    IReadOnlyDictionary<long, int> MachineReplenishmentNeedByProductId,
    IReadOnlyDictionary<long, decimal> OnOrderQuantityByProductId)
{
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
