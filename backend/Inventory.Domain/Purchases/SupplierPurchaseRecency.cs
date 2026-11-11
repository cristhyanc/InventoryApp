namespace Inventory.Domain.Purchases;

/// <summary>
/// The one rule for which of a product's recorded purchase lines is the more recent (issue #63,
/// extracted for issue #1165). Newest first is the purchase date descending, then the purchase-item
/// id descending, so a same-day tie breaks to the higher - most recently recorded - line rather than
/// to arbitrary insertion or row order.
///
/// <para><strong>It exists so there is exactly one ordering, consumed by every path that needs
/// "the latest purchase".</strong> <see cref="SupplierPriceComparisonPolicy"/> orders a product's
/// whole history with it for the price comparison, and the receipt-scan pack-size lookup
/// (<c>IReceiptScanPurchaseHistoryStore</c>, issue #1165) takes the first line of each product's
/// matched history with it. Neither writes the ordering out itself, so revising the tie-break here
/// revises it everywhere at once instead of leaving a second copy to drift (AGENTS.md
/// § Architecture rules: one authoritative calculation must feed all presentations).</para>
///
/// <para>The rule is a pure function over values the caller already has. It takes accessors rather
/// than a row type because the two callers read different projections of the same
/// <c>PurchaseItem</c> history - a full price-history entry and four columns - and neither should
/// have to fetch columns it has no use for in order to be ordered correctly.</para>
/// </summary>
public static class SupplierPurchaseRecency
{
    /// <summary>
    /// <paramref name="lines"/> newest first: <paramref name="purchaseDate"/> descending, then
    /// <paramref name="purchaseItemId"/> descending. The ordering is total over real purchase lines,
    /// because a purchase-item id is unique, so the first element is the latest line and not merely
    /// one of several equally latest ones.
    /// </summary>
    /// <typeparam name="T">The caller's own projection of a recorded purchase line.</typeparam>
    /// <param name="lines">The recorded purchase lines to order.</param>
    /// <param name="purchaseDate">Reads a line's owning purchase's date.</param>
    /// <param name="purchaseItemId">Reads a line's own <c>PurchaseItem.Id</c>.</param>
    public static IOrderedEnumerable<T> NewestFirst<T>(
        IEnumerable<T> lines,
        Func<T, DateTime> purchaseDate,
        Func<T, int> purchaseItemId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(purchaseDate);
        ArgumentNullException.ThrowIfNull(purchaseItemId);

        return lines
            .OrderByDescending(purchaseDate)
            .ThenByDescending(purchaseItemId);
    }
}
