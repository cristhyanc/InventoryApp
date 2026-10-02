namespace Inventory.Domain.Purchases;

/// <summary>
/// Guards the historical-costing invariant that a purchase movement dated at or before a
/// product's inventory-cost transition cutoff is preserved legacy history: it must not be
/// reinterpreted by a later edit. Mirrors the decision logic the former
/// <c>InventoryApi.Services.PurchaseService.EnsureLegacyPurchaseMovementsArePreservedAsync</c>/
/// <c>EnsureNoPreCutoffMovementsAsync</c>/<c>ValidatePurchaseDatesAfterBaselinesAsync</c> computed
/// inline once their database facts (baselines, existing movements) were already fetched.
/// </summary>
public static class PurchaseCostTransitionPolicy
{
    public const string PreservedMovementsMessage =
        "This purchase contains preserved pre-cutover inventory movements. Its date, products, quantities, and costs cannot be changed.";

    public static string ProtectedMovementMessage(int movementId) =>
        $"Purchase movement {movementId} is part of preserved pre-cutover history and cannot be changed or deleted.";

    public static string DateBeforeCutoffMessage(DateTime cutoffAt, long productId) =>
        $"Purchase date must be after the inventory-cost transition cutoff {cutoffAt:O} for product {productId}.";

    /// <summary>
    /// The first product among <paramref name="productIds"/> whose cutoff the proposed
    /// <paramref name="purchaseDate"/> does not come after, or <c>null</c> when none conflicts.
    /// </summary>
    public static (long ProductId, DateTime CutoffAt)? FindConflictingBaseline(
        IEnumerable<long> productIds,
        DateTime purchaseDate,
        IReadOnlyDictionary<long, DateTime> cutoffsByProduct)
    {
        foreach (var productId in productIds)
        {
            if (cutoffsByProduct.TryGetValue(productId, out var cutoffAt) && purchaseDate <= cutoffAt)
                return (productId, cutoffAt);
        }

        return null;
    }

    /// <summary>Whether any of the given movements falls at or before its product's cutoff.</summary>
    public static bool HasPreservedMovement(
        IEnumerable<(long ProductId, DateTime EffectiveAt)> movements,
        IReadOnlyDictionary<long, DateTime> cutoffsByProduct) =>
        movements.Any(movement =>
            cutoffsByProduct.TryGetValue(movement.ProductId, out var cutoffAt) && movement.EffectiveAt <= cutoffAt);

    /// <summary>
    /// Whether a purchase containing at least one preserved movement may still be changed: only
    /// when neither its date nor its items actually change.
    /// </summary>
    public static bool IsPreservedChangeAllowed(bool dateChanged, bool itemsChanged) =>
        !dateChanged && !itemsChanged;

    /// <summary>The first movement, in the given order, that falls at or before its product's cutoff.</summary>
    public static (int MovementId, long ProductId, DateTime EffectiveAt)? FindProtectedMovement(
        IEnumerable<(int MovementId, long ProductId, DateTime EffectiveAt)> movements,
        IReadOnlyDictionary<long, DateTime> cutoffsByProduct)
    {
        foreach (var movement in movements)
        {
            if (cutoffsByProduct.TryGetValue(movement.ProductId, out var cutoffAt) && movement.EffectiveAt <= cutoffAt)
                return movement;
        }

        return null;
    }
}
