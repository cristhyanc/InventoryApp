using Inventory.Application.Nayax;

namespace Inventory.Application.SalesSync;

/// <summary>
/// The outcome of persisting one coordinated batch of latest Nayax sales.
/// <see cref="EarliestCompletedSaleByProductId"/> maps each local product whose costed inventory was
/// touched by a completed sale in this batch to the earliest such sale's authorization time, which is
/// the point the inventory-cost replay has to restart from. An empty map means nothing completed
/// changed, so this batch added no rebuild work.
///
/// <para>Since issue #1103 the map is <strong>reported</strong> rather than carried to the rebuild:
/// the same save that stored the sales also recorded a durable pending rebuild per affected product,
/// so what has to be replayed survives a crash, a failed rebuild and a restart instead of living in
/// this record for the length of one call.</para>
/// </summary>
public sealed record LatestNayaxSalesPersistResult(
    IReadOnlyDictionary<long, DateTime> EarliestCompletedSaleByProductId);

/// <summary>
/// One Nayax SQS transaction message, as the store is asked to save it (issue #1103).
///
/// <para><strong>It is a separate record from the <c>lastSales</c> item on purpose.</strong> The
/// supplied status is the whole reason: an SQS message states its own
/// <c>Transaction Status ID</c> and the store must save exactly that, while every
/// <c>lastSales</c> item states none and keeps the settlement-value rule. Giving the SQS path its own
/// input type is what makes "no <c>lastSales</c> item can ever supply a status" true by construction
/// rather than by a field nobody sets.</para>
/// </summary>
/// <param name="Report">
/// The sale itself, exactly as <c>NayaxSqsTransactionParser</c> mapped it, with its
/// <c>NayaxProductId</c> resolved from the message's own <c>Product ID</c> or from the machine's
/// product map.
/// </param>
/// <param name="SuppliedTransactionStatusId">
/// The message's own <c>Transaction Status ID</c>, already classified by the parser as one of the
/// application's established ids. The store saves exactly this on an insert - never a status derived
/// from the amount - so a declined transaction that charged money is stored as declined.
/// </param>
/// <param name="MessageId">
/// The queue's own id for the delivery that stored the sale, recorded as the row's
/// <c>SqsFirstMessageId</c>. It is the one identifier of a message that is safe to persist and to
/// log; a receipt handle and a body are neither.
/// </param>
/// <param name="ReceivedAtUtc">When the reader accepted the message, recorded as <c>ReceivedViaSqsAtUtc</c>.</param>
public sealed record NayaxSqsSaleInput(
    NayaxLastSalesReport Report,
    int SuppliedTransactionStatusId,
    string MessageId,
    DateTime ReceivedAtUtc);

/// <summary>
/// What persisting one SQS message did (issue #1103).
/// </summary>
/// <param name="Inserted">
/// <see langword="true"/> when this message stored the sale, <see langword="false"/> when the
/// transaction was already stored - a redelivery, or the <c>lastSales</c> sync having got there
/// first - and the stored row was only enriched where it was still missing something.
/// </param>
/// <param name="AffectedProductIds">
/// The local products whose inventory cost this sale may have invalidated, and therefore the products
/// whose pending rebuild must be clear before the message may be deleted. Empty for a sale that
/// resolved no product, and for one that is not a completed sale: neither consumes costing
/// inventory, so neither has anything to replay.
/// </param>
public sealed record NayaxSqsSalePersistResult(
    bool Inserted,
    IReadOnlyCollection<long> AffectedProductIds);

/// <summary>
/// What draining this business's pending inventory-cost rebuilds did (issue #1103).
/// </summary>
/// <param name="StillPendingProductIds">
/// The products whose rebuild did not succeed and whose pending row therefore stays, with its
/// attempt count and failure code updated. A message that affected any of these is not deleted.
/// </param>
/// <param name="Failures">
/// One sentence per product that failed, naming the product id and the data-quality problem.
///
/// <para>The drain <strong>reports</strong> rather than throws, which issue #1103 needs on both
/// sides: the queue reader has to tell "this message's product is still failing" from "some other
/// product of this business is", because the first means keep the message and the second must not
/// stop it being deleted. The request-driven sync raises these as the one
/// <see cref="Costing.InventoryCostDataQualityException"/> it always has, so what an operator sees is
/// unchanged.</para>
/// </param>
public sealed record InventoryCostRebuildDrainResult(
    IReadOnlyCollection<long> StillPendingProductIds,
    IReadOnlyList<string> Failures);

/// <summary>
/// Narrow persistence port for the Nayax sales this application stores (issue #187, extended by
/// issue #1103), owned by the Application layer. Transaction deduplication by the remote
/// <c>TransactionID</c>, local product matching, completed/cancelled classification, historical sale
/// costing, the durable pending-rebuild record and the inventory-cost rebuild all stay behind it,
/// because each of them still depends on the persistence model; the use cases therefore stay free of
/// EF Core.
///
/// <para><strong>It is the one way a sale is stored, on both paths.</strong> The request-driven
/// <c>lastSales</c> synchronization saves a whole batch through
/// <see cref="PersistLatestSalesAsync"/>, and the queue reader saves one message at a time through
/// <see cref="PersistSqsSaleAsync"/>. Sharing the port is what makes deduplication by
/// <c>(BusinessId, TransactionID)</c>, the status rules and the rebuild bookkeeping one
/// implementation instead of two that can disagree.</para>
/// </summary>
public interface ILatestNayaxSalesStore
{
    /// <summary>
    /// Persists the batch as <c>NayaxSales</c> rows: a transaction not stored yet is added and
    /// costed, one already stored is only enriched where it is still missing its product match or
    /// status, and nothing is ever double counted. The whole batch is saved once, so a Nayax read
    /// that fails part-way through the coordinated sync leaves no partially imported refresh behind.
    ///
    /// <para>No item of this batch supplies a transaction status, so every inserted row takes the
    /// settlement-value rule it always has: a positive amount is completed, anything else is
    /// cancelled. The documented <c>lastSales</c> response carries no status field at all.</para>
    ///
    /// <para>Since issue #1103 it is also <strong>one write-locked transaction</strong>, for the
    /// reason <see cref="PersistSqsSaleAsync"/> is: this path reads "is this transaction stored?" and
    /// then inserts, the queue reader does the same thing at the same time, and without the lock both
    /// read "not stored" and the loser's whole save is refused by the unique index on
    /// <c>(BusinessId, TransactionID)</c>. With it the second writer waits and then takes the enrich
    /// path, which is a deliberate outcome rather than a lock error.</para>
    ///
    /// <para>The same save also records a <strong>pending inventory-cost rebuild</strong> for every product a completed sale in this batch affected, so a rebuild that
    /// fails afterwards is remembered rather than lost - which is the failure this path had too: the
    /// sales were already stored, so the next sync found no affected products and never retried.</para>
    /// </summary>
    Task<LatestNayaxSalesPersistResult> PersistLatestSalesAsync(
        IReadOnlyList<NayaxLastSalesReport> sales, CancellationToken cancellationToken);

    /// <summary>
    /// Persists one Nayax SQS transaction message in <strong>one write-locked transaction</strong>:
    /// it reads the transaction by <c>(BusinessId, TransactionID)</c>, inserts the new row with its
    /// SQS provenance on the same insert or applies the enrich-only path to an existing one, costs
    /// it, and records a pending inventory-cost rebuild for every product the sale affects - all of
    /// it committing together or not at all.
    ///
    /// <para>A unique-index violation on <c>(BusinessId, TransactionID)</c> - the other import path
    /// having inserted the same transaction between the read and the insert - is caught and the
    /// message is retried once as the enrich-only path, so a concurrent insert is never surfaced as a
    /// failure and never counted twice.</para>
    ///
    /// <para>It rebuilds nothing: the rebuild is the separate, drainable
    /// <see cref="RebuildInventoryCostsAsync"/> step, which is exactly what makes a sale that is
    /// saved but not yet replayed a recoverable state rather than a lost one.</para>
    /// </summary>
    Task<NayaxSqsSalePersistResult> PersistSqsSaleAsync(
        NayaxSqsSaleInput sale, CancellationToken cancellationToken);

    /// <summary>
    /// Replays the inventory cost of every product this business has a <em>pending rebuild</em>
    /// recorded for, chronologically from the earliest instant each row still has to replay from.
    ///
    /// <para>A product whose rebuild succeeds has its pending row removed in the same transaction as
    /// the rebuilt costs, so "the costs are rebuilt" and "there is nothing left to rebuild" can never
    /// disagree. A product whose rebuild fails keeps its row, with its attempt count, last attempt
    /// instant and failure code updated, and is reported in
    /// <see cref="InventoryCostRebuildDrainResult.StillPendingProductIds"/>. A row whose instant is
    /// at or before the product's transition-baseline cutoff has nothing to replay and its row is
    /// removed too - that is the baseline doing its job, not a failure.</para>
    ///
    /// <para>Every product that can be rebuilt is rebuilt and saved even when another product's
    /// history is unreplayable (issue #362), and the collected failures are reported rather than
    /// raised - see <see cref="InventoryCostRebuildDrainResult.Failures"/> for why that matters to
    /// both callers. Already persisted sales stay intact either way.</para>
    /// </summary>
    Task<InventoryCostRebuildDrainResult> RebuildInventoryCostsAsync(CancellationToken cancellationToken);
}
