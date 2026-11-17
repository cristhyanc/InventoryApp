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
        new Transaction(await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken));

    private sealed class Transaction : INayaxSalesImportTransaction
    {
        private readonly SqliteWriteLockTransaction _transaction;

        public Transaction(SqliteWriteLockTransaction transaction)
        {
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
