namespace Inventory.Application.Reorder;

/// <summary>
/// The per-product facts <see cref="CalculateReorderNeeds"/> aggregates across the machine fleet and
/// outstanding supplier orders (issue #47). Keyed by the catalogue's own product ID (the same ID
/// Nayax reports back as <c>NayaxProductID</c>) so a caller can look a product up without this use
/// case needing to know which products a particular search/category/supplier filter selected.
/// </summary>
public sealed record ReorderNeedsResult(
    IReadOnlyDictionary<long, int> MachineReplenishmentNeedByProductId,
    IReadOnlyDictionary<long, decimal> OnOrderQuantityByProductId);
