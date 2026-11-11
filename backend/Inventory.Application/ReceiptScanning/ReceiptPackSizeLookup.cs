using Inventory.Domain.ReceiptScanning;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// What a scan has to look up before a missing pack size can be suggested (issue #1165, sub-issue E
/// of #975): one supplier, and the products whose pack size the receipt did not print.
///
/// <para><strong>It decides what is worth asking for, not what the answer means.</strong> Whether a
/// figure then becomes a pack size is <c>ReceiptPackSizeSuggestionPolicy</c>'s, and the amounts it
/// judges are <c>ReceiptLineArithmeticPolicy</c>'s; this type exists so the one read of the purchase
/// history is a single statement for the whole receipt rather than one per line, and so a scan that
/// could not use an answer never asks for one.</para>
/// </summary>
/// <param name="SupplierId">The supplier the draft suggests, as this business's own supplier id.</param>
/// <param name="ProductIds">
/// The products a pack size might be suggested for: distinct, and every one of them this business's
/// own.
/// </param>
public sealed record ReceiptPackSizeLookup(int SupplierId, IReadOnlyList<long> ProductIds)
{
    /// <summary>
    /// The lookup <paramref name="payload"/> needs, or <see langword="null"/> when it needs none: no
    /// supplier of this business was suggested, or no line is both a stock line of a known product
    /// and missing its pack size.
    /// </summary>
    /// <param name="payload">The reader's validated answer.</param>
    /// <param name="productIds">Every product id this business owns, as the scan was read against.</param>
    /// <param name="supplierIds">Every supplier id this business owns.</param>
    public static ReceiptPackSizeLookup? For(
        ReceiptScanPayload payload,
        IReadOnlySet<long> productIds,
        IReadOnlySet<int> supplierIds)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(productIds);
        ArgumentNullException.ThrowIfNull(supplierIds);

        if (SuggestedSupplierId(payload, supplierIds) is not { } supplierId)
        {
            // No supplier, no history to read: the issue's rule is the last purchase from *this*
            // supplier, and another supplier's price is not evidence about this one's carton.
            return null;
        }

        var products = payload.Lines
            .Where(line => NeedsAPackSize(line) && line.ProductId is { } id && productIds.Contains(id))
            .Select(line => line.ProductId!.Value)
            .Distinct()
            .ToList();

        return products.Count == 0 ? null : new ReceiptPackSizeLookup(supplierId, products);
    }

    /// <summary>
    /// The suggested supplier of this business, or <see langword="null"/> when the reader named none
    /// or named one this business does not own.
    ///
    /// <para>The one place that answer is resolved: the draft publishes it as its suggested supplier
    /// and the pack-size lookup reads the history of it, so the supplier a pack size came from and
    /// the supplier the draft shows cannot disagree. An id this business does not own is dropped
    /// exactly as it is everywhere else in a scan - a suggestion that is not this business's own is
    /// not a suggestion (AGENTS.md § Tenant ownership and data isolation).</para>
    /// </summary>
    public static int? SuggestedSupplierId(ReceiptScanPayload payload, IReadOnlySet<int> supplierIds)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(supplierIds);

        return payload.SupplierId is { } supplierId
            && supplierId <= int.MaxValue
            && supplierIds.Contains((int)supplierId)
                ? (int)supplierId
                : null;
    }

    /// <summary>
    /// Whether a line could take a suggested pack size at all: a stock line that delivered a readable
    /// number of packs and printed no pack size.
    ///
    /// <para><strong>A pack size read from the receipt is never replaced</strong> (issue #1165), and a
    /// line whose pack count could not be read gets nothing - dividing by the unit cost alone would
    /// give the units on the whole line, which multiplied by the packs again would be twice the
    /// stock.</para>
    /// </summary>
    public static bool NeedsAPackSize(ReceiptScanPayloadLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Kind is ReceiptLineKind.Stock
            && line.UnitsPerPack is null
            && line.DeliveredPacks is > 0;
    }
}

/// <summary>
/// The last purchase of each product a pack size may be suggested from (issue #1165), as the draft
/// builder is given them.
///
/// <para><see cref="None"/> is the ordinary case and is what a scan with no supplier, no candidate
/// line or no history at all carries: an empty history suggests nothing, which leaves every line
/// exactly as the receipt printed it.</para>
/// </summary>
public sealed class ReceiptPackSizeHistory
{
    private readonly IReadOnlyDictionary<long, ReceiptScanLastPurchase> _byProduct;

    private ReceiptPackSizeHistory(IReadOnlyDictionary<long, ReceiptScanLastPurchase> byProduct) =>
        _byProduct = byProduct;

    /// <summary>No history: nothing is suggested from it.</summary>
    public static ReceiptPackSizeHistory None { get; } =
        new(new Dictionary<long, ReceiptScanLastPurchase>());

    /// <summary>
    /// The history <paramref name="purchases"/> describes. A second entry for one product is ignored
    /// rather than replacing the first: the port answers at most one line per product, and silently
    /// preferring whichever arrived last would make the answer depend on row order.
    /// </summary>
    public static ReceiptPackSizeHistory Of(IEnumerable<ReceiptScanLastPurchase> purchases)
    {
        ArgumentNullException.ThrowIfNull(purchases);

        var byProduct = new Dictionary<long, ReceiptScanLastPurchase>();
        foreach (var purchase in purchases)
        {
            byProduct.TryAdd(purchase.ProductId, purchase);
        }

        return byProduct.Count == 0 ? None : new ReceiptPackSizeHistory(byProduct);
    }

    /// <summary>
    /// The latest recorded purchase of <paramref name="productId"/> from the suggested supplier, or
    /// <see langword="null"/> when this business has never bought it from that supplier.
    /// </summary>
    public ReceiptScanLastPurchase? For(long productId) =>
        _byProduct.TryGetValue(productId, out var purchase) ? purchase : null;
}
