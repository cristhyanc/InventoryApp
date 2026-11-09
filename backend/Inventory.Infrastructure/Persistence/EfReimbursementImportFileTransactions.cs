using Inventory.Application.Imports;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IReimbursementImportFileTransactions"/> (issue #1149):
/// one <c>BEGIN IMMEDIATE</c> transaction over the worker scope's own <see cref="AppDbContext"/>,
/// the shape <see cref="EfNayaxConnectionSaveTransactions"/> (issue #537) has.
///
/// <para>It is <c>BEGIN IMMEDIATE</c> rather than SQLite's default deferred transaction for the
/// reason <see cref="SqliteWriteLockTransaction"/> explains: the file's duplicate check is a
/// <em>read</em> the write depends on, and a deferred transaction takes its write lock after that
/// read - so a concurrent writer turns the race into <c>SQLITE_BUSY</c> instead of making this one
/// wait. Taking the lock up front makes the duplicate check and the import one serialised
/// operation.</para>
///
/// <para>Every adapter the import touches - the reimbursement store, the coverage store and the job
/// store - resolves the same scoped context, so they all enlist in this one transaction without
/// knowing it exists. That is what makes "a file's rows, its coverage and its outcome commit
/// together" true rather than hoped for.</para>
/// </summary>
public sealed class EfReimbursementImportFileTransactions : IReimbursementImportFileTransactions
{
    private readonly AppDbContext _db;

    public EfReimbursementImportFileTransactions(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReimbursementImportFileTransaction> BeginAsync(
        CancellationToken cancellationToken) =>
        new Transaction(await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken));

    private sealed class Transaction : IReimbursementImportFileTransaction
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
