namespace Inventory.Application.Costing;

/// <summary>
/// Thrown by a non-dry-run <see cref="IRebuildProductCost.RebuildAsync"/> when the replayed cost
/// history has a fatal data-quality issue (issue #296; formerly
/// <c>InventoryApi.Services.InventoryCostDataQualityException</c>). It is an internal
/// data-integrity failure with a developer-facing message, not a caller-safe validation failure,
/// so the HTTP boundary deliberately does not map it and it surfaces as a logged, generic 500.
/// </summary>
public sealed class InventoryCostDataQualityException : InvalidOperationException
{
    public InventoryCostDataQualityException(string message) : base(message) { }
}
