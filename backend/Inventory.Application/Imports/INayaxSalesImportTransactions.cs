namespace Inventory.Application.Imports;

/// <summary>
/// The database transaction an uploaded Nayax sales import's find-and-save runs in (issue #1300).
/// Disposing it without <see cref="CommitAsync"/> rolls back every sale the import staged, which is
/// what makes a refused upload write nothing.
/// </summary>
public interface INayaxSalesImportTransaction : IAsyncDisposable
{
    /// <summary>Commits the imported and updated sales.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Begins the transaction the uploaded Nayax sales import's find-and-save runs in (issue #1300).
/// It is its own narrow port, named for the one operation that needs it, exactly as
/// <see cref="IReimbursementImportFileTransactions"/> (issue #1149) and
/// <c>INayaxConnectionSaveTransactions</c> (issue #537) are.
///
/// <para><strong>Why the import needs one at all.</strong> It reads "does this business already hold
/// this transaction?" and then inserts or updates accordingly - and it is no longer the only writer
/// of a Nayax sale. Since issue #1103 the SQS queue reader stores each transaction within seconds of
/// the vend, through the same unique index on <c>(BusinessId, TransactionID)</c>. A row the reader
/// stored between the import's lookup and its save therefore made the whole upload fail on that
/// index with a <c>500</c> - after the operator had waited minutes for it. Inside this transaction
/// the lookup and the save are one serialised operation: the queue reader's row is either already
/// visible to the lookup, and is updated, or waits for this commit and takes its own enrich path.</para>
///
/// <para><strong>Why it is the <em>same</em> lock the other two sales paths take.</strong> The
/// adapter opens <c>BEGIN IMMEDIATE</c>, which is what <c>EfLatestNayaxSalesStore</c>'s two save
/// paths open. A deferred transaction - SQLite's default - takes its write lock at the first write,
/// which is after the read the save was decided on, and SQLite then answers <c>SQLITE_BUSY</c>
/// rather than making the second writer wait. Taking the lock up front is what turns the race into
/// a deliberate update instead of a lock error.</para>
///
/// <para><strong>It deliberately does not span the cost replay.</strong> Every other write in the
/// application waits on this lock and gives up after the busy timeout, so what the import holds it
/// across is bounded by the row limit of
/// <c>Inventory.Domain.Imports.NayaxSalesFilePolicy</c> - the stored-row lookup, the per-row merge
/// and costing, and the one save of the sales. Replaying the affected products' inventory cost
/// happens after the commit, unlocked, exactly as it did before this change and for the reason
/// <c>EfLatestNayaxSalesStore.RebuildInventoryCostsAsync</c> drains its pending rebuilds outside the
/// lock: a replay takes as long as the products' histories, and no other writer can be made to wait
/// for that.</para>
/// </summary>
public interface INayaxSalesImportTransactions
{
    /// <summary>Begins the transaction the import's find-and-save runs in.</summary>
    Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken);
}
