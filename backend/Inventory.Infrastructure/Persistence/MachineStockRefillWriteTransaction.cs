using Inventory.Application.MachineStockSync;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The write transaction one product's Sync Restock refill batch runs in (issue #1127). It is
/// <see cref="SqliteWriteLockTransaction"/> - <c>BEGIN IMMEDIATE</c>, so SQLite's write lock is held
/// from before the events and the product are read until the batch commits - with one thing added:
/// disposal clears the request's change tracker.
///
/// <para><strong>Why it clears.</strong> A rebuild loads the product's whole movement and sale
/// history as tracked entities, and an apply request runs on one <see cref="AppDbContext"/>. Left
/// tracked, the first product's history is re-examined by every later product's
/// <c>SaveChanges</c>, so a batch got steadily slower the further through it got. Clearing after
/// each product's transaction has ended is safe precisely because nothing outside it owns that
/// state: each batch re-reads its own events and its own product inside its own lock, and a batch
/// that failed must not carry its rolled-back writes any further anyway - which is what the single
/// apply has always done on failure.</para>
/// </summary>
internal sealed class MachineStockRefillWriteTransaction : IMachineStockRefillWriteTransaction
{
    private readonly AppDbContext _db;
    private readonly SqliteWriteLockTransaction _transaction;

    private MachineStockRefillWriteTransaction(AppDbContext db, SqliteWriteLockTransaction transaction)
    {
        _db = db;
        _transaction = transaction;
    }

    public static async Task<MachineStockRefillWriteTransaction> BeginAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return new MachineStockRefillWriteTransaction(
            db,
            await SqliteWriteLockTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false));
    }

    public Task CommitAsync(CancellationToken cancellationToken) =>
        _transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // The rollback (or the release of a committed transaction) first: clearing the tracker is
        // about what the next product's saves have to look at, never about what this one wrote.
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }
}
