using System.Text.RegularExpressions;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Domain.Nayax;

/// <summary>
/// A successfully parsed Nayax Event 510 "Machine Product Changed" alert (issue #1325): the MDB
/// position whose product was replaced, the product Nayax says was taken out, and the product it
/// says was put in.
/// </summary>
public readonly record struct ParsedNayaxProductReplacement(
    int Mdb, string OutgoingProductName, string IncomingProductName);

/// <summary>
/// Deterministic, EF/HTTP-free parsing of a Nayax Event 510 "Machine Product Changed" alert's raw
/// <c>EventData</c> text (issue #1325). The one observed form, from a real <c>lastAlerts</c>
/// response, is
/// <c>&lt;operator&gt; 'has replaced product &lt;outgoing&gt; with product &lt;incoming&gt; | Product MDB: &lt;mdb&gt;</c>:
/// a single quote opens before <c>has replaced product</c> and is never closed, and nothing follows
/// the MDB number.
///
/// <para>The parser anchors on the text markers - <c>has replaced product</c>, <c>with product</c>
/// and <c>| Product MDB:</c> - and never on the operator prefix, so a different operator name, a
/// missing quote or no prefix at all parses the same way. Text that does not match, or that is
/// ambiguous because <c> with product </c> appears more than once (which name is which could then
/// only be guessed), fails to parse: the caller stores it as Needs Review with the raw text and the
/// reason, and this type never guesses a value.</para>
/// </summary>
public static class NayaxProductReplacementEventParser
{
    private const string IncomingMarker = " with product ";

    private static readonly Regex Pattern = new(
        @"has\s+replaced\s+product\s+(?<outgoing>.+?)\s+with\s+product\s+(?<incoming>.+?)\s*\|\s*Product\s+MDB:\s*(?<mdb>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static bool TryParse(
        string? eventData, out ParsedNayaxProductReplacement parsed, out string? failureReason)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(eventData))
        {
            failureReason = "EventData is empty.";
            return false;
        }

        var text = eventData.Trim();
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            failureReason =
                "EventData does not contain the supported 'has replaced product <outgoing> with product "
                + "<incoming> | Product MDB: <mdb>' form.";
            return false;
        }

        if (CountOccurrences(text, IncomingMarker) != 1)
        {
            failureReason =
                "EventData contains 'with product' more than once, so the outgoing and incoming product "
                + "names cannot be told apart.";
            return false;
        }

        if (!int.TryParse(match.Groups["mdb"].Value, out var mdb))
        {
            failureReason = "MDB code is not a valid integer.";
            return false;
        }

        var outgoing = match.Groups["outgoing"].Value.Trim();
        var incoming = match.Groups["incoming"].Value.Trim();
        if (outgoing.Length == 0 || incoming.Length == 0)
        {
            failureReason = "A product name is empty.";
            return false;
        }

        failureReason = null;
        parsed = new ParsedNayaxProductReplacement(mdb, outgoing, incoming);
        return true;
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
        }

        return count;
    }
}

/// <summary>
/// What Nayax currently reports for one MDB position of the machine, read live (issue #1325).
/// <see cref="LinkedProduct"/> is the local product the position's Nayax product id resolves to
/// through <c>Product.ExternalProductId</c>, or <see langword="null"/> when the business has none.
/// </summary>
public sealed record NayaxReplacementSlotReading(
    string? SlotProductName,
    ProductMatchCandidate? LinkedProduct,
    int? Par,
    int? MissingStockByMdb);

/// <summary>Where a suggested local product came from (issue #1325).</summary>
public enum NayaxReplacementSuggestionSource
{
    /// <summary>No product is suggested; the suggestion's reason says why.</summary>
    None = 0,

    /// <summary>The live MDB position still reports the incoming product and is linked to it.</summary>
    LiveSlot = 1,

    /// <summary>Exactly one active product of the business has the same normalised name.</summary>
    ExactName = 2,
}

/// <summary>A suggested local product, or an empty suggestion with the reason there is none.</summary>
public sealed record NayaxReplacementProductSuggestion(
    long? ProductId,
    string? ProductName,
    NayaxReplacementSuggestionSource Source,
    string? Reason)
{
    public static NayaxReplacementProductSuggestion Empty(string reason) =>
        new(null, null, NayaxReplacementSuggestionSource.None, reason);
}

/// <summary>A suggested incoming quantity, or <see langword="null"/> with the reason there is none.</summary>
public sealed record NayaxReplacementQuantitySuggestion(int? Quantity, string? Reason);

/// <summary>
/// The deterministic advice shown beside a Nayax product replacement (issue #1325). Every answer
/// here is a suggestion for the operator and nothing more: it writes nothing, decides no stock
/// movement, and is recomputed from the live machine read every time the list is shown.
/// </summary>
public static class NayaxProductReplacementSuggestionPolicy
{
    /// <summary>
    /// How close a pending Event 501 for the same machine and MDB must be to a replacement to be
    /// flagged as a possible double count. The owner confirmed on 16 November 2026 that the Adjust
    /// Stock done after a replacement creates no 501 record, so the flag is a safeguard only.
    /// </summary>
    public static readonly TimeSpan DoubleCountWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// The local product for the incoming name. The live slot comes first: when Nayax still reports
    /// a product in that MDB whose normalised name equals the incoming name, and that position is
    /// linked to a local product, that product is the suggestion. Otherwise the incoming name is
    /// matched like the outgoing one, by <see cref="SuggestByName"/>.
    /// </summary>
    public static NayaxReplacementProductSuggestion SuggestIncoming(
        ParsedNayaxProductReplacement replacement,
        NayaxReplacementSlotReading? slot,
        IReadOnlyCollection<ProductMatchCandidate> activeProducts)
    {
        ArgumentNullException.ThrowIfNull(activeProducts);

        if (slot is not null
            && SlotReportsIncoming(replacement, slot)
            && slot.LinkedProduct is { } linked)
        {
            return new NayaxReplacementProductSuggestion(
                linked.Id, linked.Name, NayaxReplacementSuggestionSource.LiveSlot, null);
        }

        var byName = SuggestByName(replacement.IncomingProductName, activeProducts);
        if (byName.ProductId is not null)
        {
            return byName;
        }

        var slotNote = SlotNote(replacement, slot);
        return slotNote is null ? byName : NayaxReplacementProductSuggestion.Empty($"{slotNote} {byName.Reason}");
    }

    /// <summary>The local product for the outgoing name: an exact, unique name match or nothing.</summary>
    public static NayaxReplacementProductSuggestion SuggestOutgoing(
        ParsedNayaxProductReplacement replacement,
        IReadOnlyCollection<ProductMatchCandidate> activeProducts)
    {
        ArgumentNullException.ThrowIfNull(activeProducts);
        return SuggestByName(replacement.OutgoingProductName, activeProducts);
    }

    /// <summary>
    /// A unique match on <see cref="ProductMatcher.NormalizeName"/> among the business's active
    /// products. A tie or no match leaves the suggestion empty with the reason.
    /// </summary>
    public static NayaxReplacementProductSuggestion SuggestByName(
        string reportedName, IReadOnlyCollection<ProductMatchCandidate> activeProducts)
    {
        ArgumentNullException.ThrowIfNull(activeProducts);

        var normalised = ProductMatcher.NormalizeName(reportedName);
        var matches = activeProducts
            .Where(product => NamesEqual(product.Name, normalised))
            .Take(2)
            .ToList();

        return matches switch
        {
            [var only] => new NayaxReplacementProductSuggestion(
                only.Id, only.Name, NayaxReplacementSuggestionSource.ExactName, null),
            [] => NayaxReplacementProductSuggestion.Empty($"No active product is named '{reportedName}'."),
            _ => NayaxReplacementProductSuggestion.Empty(
                $"More than one active product is named '{reportedName}'."),
        };
    }

    /// <summary>
    /// The units Nayax currently shows in the MDB, <c>PAR - MissingStockByMDB</c> - the same
    /// arithmetic the Pick List uses for "current". The operator's Adjust Stock in Nayax has already
    /// filled the slot, so this is the closest figure to what was loaded (owner's decision, 16
    /// November 2026); it can read low by the sales since the replacement and the operator edits
    /// it. It is offered only when the slot still reports the incoming product, both values are
    /// present, the result is not negative, and no newer pending event exists for the same machine
    /// and MDB; otherwise it is <see langword="null"/> with the reason.
    /// </summary>
    public static NayaxReplacementQuantitySuggestion SuggestIncomingQuantity(
        ParsedNayaxProductReplacement replacement,
        NayaxReplacementSlotReading? slot,
        bool newerPendingEventExists)
    {
        if (slot is null)
        {
            return new(null, $"Nayax does not currently report MDB {replacement.Mdb} on this machine.");
        }

        if (!SlotReportsIncoming(replacement, slot))
        {
            return new(null, SlotNote(replacement, slot));
        }

        if (newerPendingEventExists)
        {
            return new(
                null,
                $"A newer pending event exists for MDB {replacement.Mdb}, so the slot's current units "
                + "may not be what this replacement loaded.");
        }

        if (slot.Par is not int par || slot.MissingStockByMdb is not int missing)
        {
            return new(null, $"Nayax does not report both PAR and missing stock for MDB {replacement.Mdb}.");
        }

        var current = par - missing;
        if (current < 0)
        {
            return new(
                null,
                $"Nayax reports more missing units ({missing}) than PAR ({par}) for MDB {replacement.Mdb}.");
        }

        return new(current, null);
    }

    /// <summary>
    /// Whether a pending Event 501 at <paramref name="stockAdjustmentAt"/> for the same machine and
    /// MDB is close enough to the replacement at <paramref name="replacementAt"/> to be a possible
    /// double count. Inclusive of the window's boundary, either side of the replacement.
    /// </summary>
    public static bool IsPossibleDoubleCount(DateTime replacementAt, DateTime stockAdjustmentAt) =>
        (stockAdjustmentAt - replacementAt).Duration() <= DoubleCountWindow;

    private static bool SlotReportsIncoming(
        ParsedNayaxProductReplacement replacement, NayaxReplacementSlotReading slot) =>
        !string.IsNullOrWhiteSpace(slot.SlotProductName)
        && NamesEqual(slot.SlotProductName, ProductMatcher.NormalizeName(replacement.IncomingProductName));

    private static string? SlotNote(ParsedNayaxProductReplacement replacement, NayaxReplacementSlotReading? slot)
    {
        if (slot is null)
        {
            return $"Nayax does not currently report MDB {replacement.Mdb} on this machine.";
        }

        if (!SlotReportsIncoming(replacement, slot))
        {
            return string.IsNullOrWhiteSpace(slot.SlotProductName)
                ? $"Nayax reports no product in MDB {replacement.Mdb} now."
                : $"MDB {replacement.Mdb} now reports '{slot.SlotProductName}', not '{replacement.IncomingProductName}'.";
        }

        return slot.LinkedProduct is null
            ? $"MDB {replacement.Mdb} is not linked to a local product."
            : null;
    }

    private static bool NamesEqual(string name, string normalisedOther) =>
        string.Equals(ProductMatcher.NormalizeName(name), normalisedOther, StringComparison.OrdinalIgnoreCase);
}
