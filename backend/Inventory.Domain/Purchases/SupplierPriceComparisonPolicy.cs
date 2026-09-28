namespace Inventory.Domain.Purchases;

/// <summary>
/// One recorded <c>PurchaseItem.UnitCost</c> for a product, joined to its owning Purchase's date,
/// title/reference, and supplier (nullable - a Purchase may have no supplier recorded).
/// </summary>
public readonly record struct SupplierPriceHistoryEntry(
    int PurchaseItemId,
    int PurchaseId,
    string PurchaseTitle,
    DateTime PurchaseDate,
    int? SupplierId,
    string? SupplierName,
    decimal UnitCost);

/// <summary>
/// The lowest-vs-latest comparison for a product's actual Purchase history, and every recorded
/// entry newest-first for the drill-down view. <see cref="PercentageDifference"/> is only populated
/// when <see cref="PercentageIsMeaningful"/> is true (a zero lowest cost makes the percentage
/// undefined, not zero or infinite).
/// </summary>
public readonly record struct SupplierPriceComparison(
    SupplierPriceHistoryEntry? Lowest,
    SupplierPriceHistoryEntry? Latest,
    decimal? AbsoluteDifference,
    decimal? PercentageDifference,
    bool PercentageIsMeaningful,
    IReadOnlyList<SupplierPriceHistoryEntry> HistoryNewestFirst);

/// <summary>
/// Derives the supplier-product price comparison from actual, immutable <c>PurchaseItem</c> history
/// (issue #63). The one authoritative calculation: callers (the Application use case, the API, and
/// ultimately Angular) only fetch history and present this result, never recompute lowest/latest/diff
/// themselves.
/// </summary>
public static class SupplierPriceComparisonPolicy
{
    public static SupplierPriceComparison Evaluate(IReadOnlyList<SupplierPriceHistoryEntry> entries)
    {
        // Newest first; a same-day tie is broken by the higher (most recently recorded) purchase
        // item id, so the choice is deterministic rather than arbitrary insertion order.
        var historyNewestFirst = entries
            .OrderByDescending(entry => entry.PurchaseDate)
            .ThenByDescending(entry => entry.PurchaseItemId)
            .ToList();

        if (historyNewestFirst.Count == 0)
        {
            return new SupplierPriceComparison(null, null, null, null, false, historyNewestFirst);
        }

        var latest = historyNewestFirst[0];

        // Equal-lowest-cost ties are broken by the earliest occurrence (the first time that price
        // was recorded), then by the lower purchase item id, so the tie-break is deterministic. No
        // tied record is discarded: every one of them still appears in historyNewestFirst.
        var lowest = entries
            .OrderBy(entry => entry.UnitCost)
            .ThenBy(entry => entry.PurchaseDate)
            .ThenBy(entry => entry.PurchaseItemId)
            .First();

        var absoluteDifference = latest.UnitCost - lowest.UnitCost;
        var percentageIsMeaningful = lowest.UnitCost != 0m;
        var percentageDifference = percentageIsMeaningful
            ? absoluteDifference / lowest.UnitCost * 100m
            : (decimal?)null;

        return new SupplierPriceComparison(
            lowest,
            latest,
            absoluteDifference,
            percentageDifference,
            percentageIsMeaningful,
            historyNewestFirst);
    }
}
