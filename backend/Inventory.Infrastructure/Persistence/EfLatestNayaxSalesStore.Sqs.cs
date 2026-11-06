using Inventory.Application.SalesSync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Saving one Nayax SQS transaction message (issue #1103), beside the rest of
/// <see cref="EfLatestNayaxSalesStore"/> because it shares every rule with it - the deduplication,
/// the product match, the status rules, the costing and the pending-rebuild record - and differs in
/// exactly two ways: it is one message rather than a batch, and it is one
/// <c>BEGIN IMMEDIATE</c> transaction of its own.
/// </summary>
public sealed partial class EfLatestNayaxSalesStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// The columns the per-business transaction index is over, as they appear in SQLite's own
    /// violation message. Matching on them is what keeps the translation below about <em>that</em>
    /// constraint rather than about any failed save.
    /// </summary>
    private const string TransactionIndexColumns = "NayaxSales.BusinessId, NayaxSales.TransactionID";

    /// <inheritdoc />
    public async Task<NayaxSqsSalePersistResult> PersistSqsSaleAsync(
        NayaxSqsSaleInput sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);

        try
        {
            return await PersistSqsSaleOnceAsync(sale, enrichOnly: false, cancellationToken);
        }
        catch (DbUpdateException failure) when (IsDuplicateTransaction(failure))
        {
            // The other import path inserted this very transaction between this transaction's read
            // and its insert. The database refusing the second insert is the deduplication working,
            // not a failure to report: the sale exists, so the message is retried once as the
            // enrich-only path and is neither counted twice nor surfaced as an error.
            //
            // The tracker is cleared because the failed save rolled back and everything it staged -
            // the refused sale and the pending rebuild beside it - describes a world that does not
            // exist. This context belongs to this one message's own scope, so there is nothing else
            // of anybody's in it to lose.
            _db.ChangeTracker.Clear();

            return await PersistSqsSaleOnceAsync(sale, enrichOnly: true, cancellationToken);
        }
    }

    /// <summary>
    /// One attempt: one write-locked transaction that reads the transaction by
    /// <c>(BusinessId, TransactionID)</c> - the business half coming from the tenant query filter, as
    /// everywhere else - inserts or enriches it, costs it, records what has to be replayed, and
    /// commits all of it together.
    ///
    /// <para><strong>Why <c>BEGIN IMMEDIATE</c>.</strong> The read this insert is decided on and the
    /// insert itself have to be one operation. A deferred transaction takes SQLite's write lock at
    /// the first write, which is after that read, so two writers both read "not stored" and the
    /// loser's whole save fails with <c>SQLITE_BUSY</c> instead of waiting. Taking the write lock
    /// when the transaction opens makes the second writer wait and then read the first one's
    /// committed state - which is what turns the race between this reader and the request-driven sync
    /// into the enrich path rather than a lock error. The unique index stays the backstop under it,
    /// because a lock is not a constraint.</para>
    /// </summary>
    /// <param name="sale">The message to store, with its supplied status and its provenance.</param>
    /// <param name="enrichOnly">
    /// <see langword="true"/> for the one retry after a duplicate-key refusal. The row must exist by
    /// then - the other writer committed it, and this transaction holds the write lock before it
    /// reads - so an attempt that would still insert is a contradiction and is refused rather than
    /// turned into a second row.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads, the save and the commit.</param>
    private async Task<NayaxSqsSalePersistResult> PersistSqsSaleOnceAsync(
        NayaxSqsSaleInput sale, bool enrichOnly, CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var affected = new Dictionary<long, DateTime>();
        var context = await ReadMatchingContextAsync([sale.Report], cancellationToken);

        var application = await ApplySaleAsync(
            sale.Report,
            sale.SuppliedTransactionStatusId,

            // Provenance belongs to the insert that creates the row, so the retry carries none: the
            // row it is about to enrich was created by the other path, and saying it arrived from the
            // queue would be untrue.
            enrichOnly ? null : new SqsProvenance(sale.MessageId, sale.ReceivedAtUtc),
            context,
            affected,
            cancellationToken);

        if (!application.Stored)
        {
            // Unreachable: NayaxSqsTransactionParser refuses a message with no readable
            // "Authorization Time" long before the store sees it, so a saved SQS outcome always
            // carries an authoritative instant. Stated rather than assumed, because the alternative
            // is reporting a sale as saved when nothing was.
            throw new InvalidOperationException(
                "A Nayax SQS sale reached the store with no authoritative authorization instant.");
        }

        if (enrichOnly && application.Inserted)
        {
            throw new InvalidOperationException(
                "A Nayax SQS sale was refused as a duplicate and then found to be absent, so the "
                    + "transaction it names is in neither state. Nothing was saved.");
        }

        await RecordPendingRebuildsAsync(affected, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new NayaxSqsSalePersistResult(
            application.Inserted,

            // What has to be clear before the message may be deleted: this sale's own product, for an
            // already stored row as much as for a freshly inserted one. See
            // SaleApplication.CompletedSaleProductId.
            application.CompletedSaleProductId is { } productId ? [productId] : []);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is the unique index on <c>(BusinessId, TransactionID)</c>
    /// refusing a second row for one transaction - and not some other failed save.
    /// </summary>
    private static bool IsDuplicateTransaction(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(TransactionIndexColumns, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
