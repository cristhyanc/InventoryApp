using Inventory.Domain.Nayax;

namespace Inventory.Application.SaleTimestampRepair;

/// <summary>
/// The one definition of which stored sales a sale timestamp repair examines (issue #472), shared by
/// the preview that decides them and the apply that re-reads them.
///
/// It is deliberately wider than "the transactions a source named". The affected range is derived
/// from the evidence - the earliest and latest instant it covers, widened to the stored instants of
/// the sales it names, because a shifted row's stored value can sit many hours outside the
/// authoritative range - and <em>every</em> stored sale inside that range is examined. A sale no
/// source covered is then reported as explicitly unresolved rather than being invisible, which is
/// what makes a boundary cohort an operator can see and obtain evidence for, instead of a silent
/// difference between a report and an export. Nothing here assumes a week, a day or any other fixed
/// period (AGENTS.md § Reporting dates and filters).
///
/// The apply re-reads exactly the same set inside its transaction, so a sale <em>added</em> inside
/// the examined range between the preview and the apply makes the plan stale rather than being
/// repaired around.
/// </summary>
internal static class NayaxSaleTimestampRepairSales
{
    /// <summary>
    /// The examined sales: the ones the given transactions name, plus every stored sale in the
    /// range, de-duplicated by transaction and ordered deterministically so two reads of an
    /// unchanged database produce the same plan.
    /// </summary>
    public static async Task<IReadOnlyList<StoredNayaxSale>> ReadAsync(
        INayaxSaleTimestampRepairStore store,
        IReadOnlyCollection<long> transactionIds,
        DateTime? fromUtcInclusive,
        DateTime? toUtcInclusive,
        CancellationToken cancellationToken)
    {
        var named = await store.ListSalesByTransactionIdAsync(transactionIds, cancellationToken);
        var inRange = fromUtcInclusive is { } from && toUtcInclusive is { } to
            ? await store.ListSalesInRangeAsync(from, to, cancellationToken)
            : [];

        return named.Concat(inRange)
            .GroupBy(sale => sale.TransactionId)
            .Select(group => group.First())
            .OrderBy(sale => sale.StoredInstantUtc)
            .ThenBy(sale => sale.TransactionId)
            .ToList();
    }

    /// <summary>
    /// The affected range: the earliest and latest instant among the readable evidence values, the
    /// stored instants of the sales that evidence names, and - when one was requested - the
    /// reconciliation window, so every sale a reconciled Sydney day contains is examined. It is
    /// <c>null</c> only when there is nothing at all to bound it with.
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) Range(
        IReadOnlyCollection<NayaxSaleTimestampEvidence> evidence,
        IReadOnlyCollection<StoredNayaxSale> namedSales,
        (DateTime FromUtc, DateTime ToUtc)? reconciliationWindowUtc = null)
    {
        var instants = evidence
            .Where(item => item.AuthorizationInstantUtc.HasValue)
            .Select(item => item.AuthorizationInstantUtc!.Value)
            .Concat(namedSales.Select(sale => sale.StoredInstantUtc))
            .ToList();
        if (reconciliationWindowUtc is { } window)
        {
            instants.Add(window.FromUtc);
            instants.Add(window.ToUtc);
        }

        return instants.Count == 0 ? (null, null) : (instants.Min(), instants.Max());
    }
}
