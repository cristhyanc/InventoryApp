using Inventory.Domain.Gst;

namespace Inventory.Domain.Purchases;

/// <summary>One stored purchase line, as matching an edit against it needs to see it.</summary>
public readonly record struct StoredPurchaseLine(int Id, long ProductId, GstClassificationState Gst);

/// <summary>
/// One submitted purchase line's identity: the stable line id when the caller sent one, and the
/// product it names. A <c>null</c> <see cref="Id"/> is a caller that did not identify the line -
/// either a new line, or an existing client that predates line ids.
/// </summary>
public readonly record struct SubmittedPurchaseLine(int? Id, long ProductId);

/// <summary>
/// Which stored line each submitted line updates, in submission order, with <c>null</c> for a line
/// that is new. <see cref="Error"/> is the caller-safe validation message when the submission's
/// identity cannot be determined safely; <see cref="StoredLineIds"/> is then empty and must not be
/// used.
/// </summary>
public readonly record struct PurchaseLineMatch(IReadOnlyList<int?> StoredLineIds, string? Error);

/// <summary>
/// Decides which stored purchase line each submitted line of an edit refers to (issue #429).
///
/// Identity matters because a line carries accounting state the request does not resend: its GST
/// classification and the provenance of that classification are kept when the caller does not
/// resubmit them. Matching only by product id is enough while a purchase has at most one line per
/// product, but a real invoice can hold two lines for the same product - two cases of the same
/// drink at different unit costs, say - and then removing or reordering lines would hand one line's
/// classification to the other.
///
/// So the rule is:
/// <list type="bullet">
///   <item>a submitted line that carries an id updates exactly that stored line;</item>
///   <item>an id must be one of this purchase's own stored line ids, must appear once, and must
///   name the product the stored line already holds;</item>
///   <item>a submitted line with no id falls back to first-in-first-out matching by product among
///   the stored lines no id claimed - the behaviour every existing client already relies on;</item>
///   <item>except that the fallback refuses to guess: when several unclaimed stored lines of that
///   product disagree about their GST classification state, there is no safe answer, and the
///   submission is rejected so the caller can identify its lines instead.</item>
/// </list>
/// Duplicate-product lines that agree about their classification - which is every purchase that
/// existed before issue #429, all of them <c>Unknown</c>/<c>Unknown</c> - still match exactly as
/// they did, because whichever one is matched carries the same state.
/// </summary>
public static class PurchaseLineIdentityPolicy
{
    /// <summary>One id submitted twice names two different lines; the caller has to say which.</summary>
    public const string DuplicateLineIdMessage = "A purchase line id may be submitted only once.";

    /// <summary>
    /// Covers an id that does not exist, one that belongs to a different purchase, and one that
    /// belongs to another business: the caller's own purchase is read through the tenant query
    /// filters, so another business's line is simply not among its lines, and all three cases get
    /// the same answer rather than revealing which it was.
    /// </summary>
    public const string UnknownLineIdMessage = "A submitted purchase line id does not belong to this purchase.";

    /// <summary>
    /// Re-pointing an identified line at a different product would move its classification and its
    /// restock movement to another product's costing history. Removing the line and adding a new
    /// one states that intent instead.
    /// </summary>
    public const string ProductChangedMessage =
        "A purchase line's product cannot be changed. Remove the line and add a new one instead.";

    /// <summary>The refusal to guess which duplicate-product line a classification belongs to.</summary>
    public const string AmbiguousLineMessage =
        "This purchase has more than one line for the same product with different GST classifications. " +
        "Submit each line's id so the right line is updated.";

    /// <summary>
    /// Matches <paramref name="submitted"/> against <paramref name="stored"/>, or reports the one
    /// reason the submission cannot be matched safely.
    /// </summary>
    public static PurchaseLineMatch Match(
        IReadOnlyList<StoredPurchaseLine> stored,
        IReadOnlyList<SubmittedPurchaseLine> submitted)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(submitted);

        var storedById = stored.ToDictionary(line => line.Id);
        var identified = new HashSet<int>();
        foreach (var line in submitted)
        {
            if (line.Id is not { } id) continue;
            if (!identified.Add(id)) return Rejected(DuplicateLineIdMessage);
            if (!storedById.TryGetValue(id, out var match)) return Rejected(UnknownLineIdMessage);
            if (match.ProductId != line.ProductId) return Rejected(ProductChangedMessage);
        }

        var unclaimedByProduct = new Dictionary<long, Queue<StoredPurchaseLine>>();
        foreach (var line in stored)
        {
            if (identified.Contains(line.Id)) continue;
            if (!unclaimedByProduct.TryGetValue(line.ProductId, out var candidates))
                unclaimedByProduct[line.ProductId] = candidates = new Queue<StoredPurchaseLine>();
            candidates.Enqueue(line);
        }

        var matched = new List<int?>(submitted.Count);
        foreach (var line in submitted)
        {
            if (line.Id is { } id)
            {
                matched.Add(id);
                continue;
            }

            if (!unclaimedByProduct.TryGetValue(line.ProductId, out var candidates) || candidates.Count == 0)
            {
                matched.Add(null);
                continue;
            }

            if (candidates.Count > 1 && candidates.Select(candidate => candidate.Gst).Distinct().Count() > 1)
                return Rejected(AmbiguousLineMessage);

            matched.Add(candidates.Dequeue().Id);
        }

        return new PurchaseLineMatch(matched, null);
    }

    private static PurchaseLineMatch Rejected(string message) => new([], message);
}
