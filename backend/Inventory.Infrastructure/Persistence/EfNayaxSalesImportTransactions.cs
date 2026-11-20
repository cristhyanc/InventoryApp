using Inventory.Application.Imports;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSalesImportTransactions"/> (issue #1300): one
/// <c>BEGIN IMMEDIATE</c> transaction over the request's own <see cref="AppDbContext"/>, the shape
/// <see cref="EfReimbursementImportFileTransactions"/> (issue #1149) and
/// <see cref="EfNayaxConnectionSaveTransactions"/> (issue #537) have.
///
/// <para>It is deliberately the <em>same</em> <see cref="SqliteWriteLockTransaction"/> the two paths
/// of <see cref="EfLatestNayaxSalesStore"/> open (issue #1103). All three write a Nayax sale through
/// the same unique index on <c>(BusinessId, TransactionID)</c> after reading whether that
/// transaction is already stored, so taking one lock is what serialises them into "found it, update
/// it" instead of leaving the loser's save to be refused by the index.</para>
///
/// <para>The import's store resolves the same scoped context, so its lookup, its staged sales and
/// its save all enlist in this transaction without knowing it exists - and the cost replay that
/// follows the commit runs outside it, which is what bounds how long the lock is held to the file's
/// own row limit.</para>
/// </summary>
public sealed class EfNayaxSalesImportTransactions : INayaxSalesImportTransactions
{
    private readonly AppDbContext _db;

    public EfNayaxSalesImportTransactions(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken) =>
        new Transaction(await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken), _db);

    private sealed class Transaction : INayaxSalesImportTransaction
    {
        private readonly SqliteWriteLockTransaction _transaction;
        private readonly AppDbContext _db;

        public Transaction(SqliteWriteLockTransaction transaction, AppDbContext db)
        {
            _transaction = transaction;
            _db = db;
        }

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        /// <summary>
        /// Detaches everything the context is tracking, which is what the rollback this transaction
        /// is about to make leaves behind: the batch's added and modified sales, its staged pending
        /// cost rebuilds and the progress staged onto the job row. Nothing of the batch survives it,
        /// so the save that records the failure writes only what it loads afterwards - from the
        /// database, which is where the durable progress is.
        /// </summary>
        public void DiscardStagedChanges() => _db.ChangeTracker.Clear();

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
