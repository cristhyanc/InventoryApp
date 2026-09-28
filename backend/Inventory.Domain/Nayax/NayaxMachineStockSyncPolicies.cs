using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Domain.Nayax;

/// <summary>
/// The minimal storage-product shape the Sync Restock rules need: an identity, a display name, and
/// the physical storage quantity available to hand out to a machine. Deliberately not the
/// persistence <c>Product</c> entity, so these rules stay free of EF Core and InventoryApi.
/// </summary>
public readonly record struct NayaxStockSyncProduct(long Id, string Name, int QuantityInStock);

/// <summary>
/// A previously recorded manual machine refill, used only as duplicate-detection evidence.
/// <see cref="QuantityChange"/> is the movement's own signed change to storage, so a manual refill
/// of four units is <c>-4</c>.
/// </summary>
public readonly record struct ManualRefillEvidence(long ProductId, int QuantityChange, DateTime EffectiveAt);

/// <summary>How one imported alert resolved against the machine's MDB map and the local catalogue.</summary>
public readonly record struct NayaxStockEventResolution(
    NayaxStockEventMatchStatus MatchStatus,
    long? MatchedProductId,
    string? NeedsReviewReason);

/// <summary>
/// Deterministic resolution of a parsed Nayax Event 501 stock adjustment to a local product
/// (issue #183). Machine + MDB is the primary mapping; the alert's product name is only a tolerant
/// validation check, normalised through the same authoritative
/// <see cref="ProductMatcher.NormalizeName"/> used for sale-to-product matching. Anything that
/// cannot be resolved safely is Needs Review with a reason and never causes an inventory movement;
/// this policy never guesses a product.
/// </summary>
public static class NayaxMachineStockMatchPolicy
{
    public static NayaxStockEventResolution Resolve(
        long machineId,
        ParsedNayaxStockAdjustment parsed,
        IReadOnlyDictionary<int, long> nayaxProductIdByMdb,
        IReadOnlyDictionary<long, NayaxStockSyncProduct> localProductsByNayaxId)
    {
        if (!nayaxProductIdByMdb.TryGetValue(parsed.Mdb, out var nayaxProductId))
            return Unresolved($"MDB {parsed.Mdb} is not a known position on machine {machineId}.");

        if (!localProductsByNayaxId.TryGetValue(nayaxProductId, out var product))
        {
            return Unresolved(
                $"No local product is mapped to Nayax product {nayaxProductId} (MDB {parsed.Mdb}).");
        }

        // The product is identified; a name disagreement is a material mismatch to review rather
        // than a failed lookup, so the resolved id is still reported for the operator's context.
        if (!string.Equals(
                ProductMatcher.NormalizeName(product.Name),
                ProductMatcher.NormalizeName(parsed.ProductName),
                StringComparison.OrdinalIgnoreCase))
        {
            return new NayaxStockEventResolution(
                NayaxStockEventMatchStatus.NeedsReview,
                product.Id,
                $"Product name mismatch: Nayax reported '{parsed.ProductName}' but MDB {parsed.Mdb} is mapped to '{product.Name}'.");
        }

        return new NayaxStockEventResolution(NayaxStockEventMatchStatus.Matched, product.Id, null);
    }

    /// <summary>The resolution for an alert whose EventData could not be parsed at all.</summary>
    public static NayaxStockEventResolution Unparsed(string? failureReason) =>
        new(NayaxStockEventMatchStatus.NeedsReview, null, failureReason);

    private static NayaxStockEventResolution Unresolved(string reason) =>
        new(NayaxStockEventMatchStatus.NeedsReview, null, reason);
}

/// <summary>The storage consequences of one pending event, as shown in the reconciliation preview.</summary>
public readonly record struct NayaxStockEventStorageImpact(
    bool IsPositiveRefill,
    bool IsDiscrepancy,
    bool IsInsufficientStorage,
    int? UnaccountedDifference);

/// <summary>
/// Deterministic reading of what a pending Nayax stock-adjustment event would do to storage
/// (issue #183). A positive quantity is a refill out of storage; a negative quantity is a machine
/// discrepancy that never increases storage. A refill larger than the available storage quantity is
/// surfaced with its unaccounted difference rather than reduced to what is available.
/// </summary>
public static class NayaxMachineStockImpactPolicy
{
    public static NayaxStockEventStorageImpact Evaluate(
        NayaxStockEventMatchStatus matchStatus,
        int? parsedQuantity,
        int? availableStorageQuantity)
    {
        var isPositiveRefill = matchStatus == NayaxStockEventMatchStatus.Matched && parsedQuantity is > 0;
        var isInsufficient = isPositiveRefill
            && availableStorageQuantity.HasValue
            && parsedQuantity!.Value > availableStorageQuantity.Value;

        return new NayaxStockEventStorageImpact(
            isPositiveRefill,
            parsedQuantity is < 0,
            isInsufficient,
            isInsufficient ? parsedQuantity!.Value - availableStorageQuantity!.Value : null);
    }
}

/// <summary>
/// Duplicate detection between an imported Nayax event and an operator's earlier manual refill
/// (issue #183). It only ever produces an informational flag for human resolution: nothing here
/// blocks, merges, or automatically deducts.
/// </summary>
public static class NayaxMachineStockDuplicatePolicy
{
    /// <summary>
    /// A manual refill recorded within this window of a Nayax event for the same machine, product,
    /// and quantity is a plausible duplicate requiring human resolution, not an automatic double
    /// deduction. This is a deliberately generous same-day-or-so window.
    /// </summary>
    public static readonly TimeSpan DetectionWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// The first manual refill that plausibly records the same physical restock, or <c>null</c>.
    /// Only an exact same-product, same-quantity movement inside the window qualifies, so an
    /// unrelated nearby manual movement is not reported as a duplicate.
    /// </summary>
    public static ManualRefillEvidence? FindPossibleDuplicate(
        long matchedProductId,
        int positiveQuantity,
        DateTime eventTimestamp,
        IReadOnlyList<ManualRefillEvidence> manualRefills)
    {
        foreach (var refill in manualRefills)
        {
            if (refill.ProductId == matchedProductId
                && refill.QuantityChange == -positiveQuantity
                && (refill.EffectiveAt - eventTimestamp).Duration() <= DetectionWindow)
            {
                return refill;
            }
        }

        return null;
    }
}

/// <summary>What applying one imported event must do to storage inventory.</summary>
public enum NayaxStockEventApplyDecision
{
    /// <summary>Apply the refill: reduce storage by the reported quantity.</summary>
    Apply,

    /// <summary>Already applied; re-applying must never deduct a second time.</summary>
    AlreadyApplied,

    /// <summary>Unparsed, unmatched, or materially mismatched; never applied.</summary>
    NeedsReview,

    /// <summary>A negative adjustment: a machine discrepancy, never a refill.</summary>
    NegativeDiscrepancy,

    /// <summary>More units than storage holds; never partially applied, storage never negative.</summary>
    InsufficientStorage,

    /// <summary>The matched product no longer exists locally.</summary>
    MatchedProductMissing
}

/// <summary>A decision plus the explanation shown to the operator.</summary>
public readonly record struct NayaxStockEventApplyRuling(NayaxStockEventApplyDecision Decision, string Message);

/// <summary>
/// The deterministic rules deciding whether one imported Nayax stock-adjustment event may reduce
/// storage inventory (issue #183). Every refusal is explicit and explained; nothing here reads or
/// writes storage itself.
/// </summary>
public static class NayaxMachineStockApplyPolicy
{
    public static NayaxStockEventApplyRuling Decide(
        NayaxStockEventProcessingStatus processingStatus,
        NayaxStockEventMatchStatus matchStatus,
        string? needsReviewReason,
        long? matchedProductId,
        int? parsedQuantity,
        int? availableStorageQuantity)
    {
        if (processingStatus == NayaxStockEventProcessingStatus.Applied)
            return new(NayaxStockEventApplyDecision.AlreadyApplied, "Already applied.");

        if (matchStatus != NayaxStockEventMatchStatus.Matched || matchedProductId is null || parsedQuantity is null)
        {
            return new(
                NayaxStockEventApplyDecision.NeedsReview,
                needsReviewReason ?? "Event needs review and cannot be applied.");
        }

        if (parsedQuantity.Value <= 0)
        {
            return new(
                NayaxStockEventApplyDecision.NegativeDiscrepancy,
                "Negative adjustments are machine discrepancies, not refills, and are never applied automatically.");
        }

        if (availableStorageQuantity is not int available)
            return new(NayaxStockEventApplyDecision.MatchedProductMissing, "Matched product no longer exists.");

        if (parsedQuantity.Value > available)
        {
            return new(
                NayaxStockEventApplyDecision.InsufficientStorage,
                $"Nayax reported {parsedQuantity.Value} loaded but only {available} is available in storage.");
        }

        return new(NayaxStockEventApplyDecision.Apply, "Applied.");
    }
}
