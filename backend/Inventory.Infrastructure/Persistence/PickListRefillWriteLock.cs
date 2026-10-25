using System.Data;
using Inventory.Application.PickList;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The write lock both pick-list refill adapters take, and the one place the per-business idempotency
/// key index's violation is recognised. It is shared rather than duplicated because
/// <see cref="EfPickListRefillStore"/> (issue #743) and
/// <see cref="EfMultiMachinePickListRefillStore"/> (issue #897) write the same two tables under the
/// same key namespace: two copies of this could drift apart, and a refill endpoint whose lock or whose
/// duplicate-key translation differs from its sibling's is exactly the inconsistency the lock exists
/// to prevent.
///
/// <para><strong>It asks SQLite for <c>BEGIN IMMEDIATE</c>, like
/// <see cref="EfBusinessMembershipWriteStore"/> (issue #522) and
/// <see cref="EfNayaxConnectionSaveTransactions"/> (issue #537), and for the same reason.</strong> A
/// deferred transaction - the default - takes its write lock at the first write, which is
/// <em>after</em> the reads a refill is decided on: the idempotency key claim, the machines, and the
/// storage quantity each product's total is checked against. Two pickers would both read "enough
/// stock" and both deduct, and two copies of one submission would both read "no refill under this key"
/// and both record one. <c>BEGIN IMMEDIATE</c> takes the write lock when the transaction opens, so the
/// second caller waits (within <c>Default Timeout</c>) and then reads the first one's committed state -
/// which is what makes the loser's refusal a deliberate not-enough-stock sentence, or its retry the
/// original result, rather than a lock error.</para>
///
/// <para>A non-SQLite relational provider falls back to its default transaction - still one
/// transaction around the claim, the checks and the writes - and the EF InMemory provider some tests
/// use has no transaction at all, exactly as the other stores in this folder treat it. Neither
/// fallback is a supported production configuration (docs/architecture.md § SQLite operating
/// assumptions).</para>
/// </summary>
internal static class PickListRefillWriteLock
{
    /// <summary>
    /// The columns of the per-business unique index on the key, as SQLite names them in a
    /// constraint-violation message. Both have to appear, so a violation of any other constraint on
    /// this table is left untranslated.
    /// </summary>
    private static readonly string[] IdempotencyKeyIndexColumns =
        ["PickListRefills.BusinessId", "PickListRefills.IdempotencyKey"];

    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// Opens the transaction the whole refill runs in, held before the first read rather than at the
    /// first write.
    /// </summary>
    public static async Task<IPickListRefillWriteTransaction> BeginAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
        {
            // Nothing to begin, or already inside somebody else's transaction: that caller owns the
            // commit, and starting a second one here would either fail or commit half of it.
            return new WriteTransaction(null, null, null, null);
        }

        var connection = db.Database.GetDbConnection();

        // Only closed if this transaction opened it: a test that shares one open connection across
        // contexts, and a request that already has one, must keep theirs.
        DatabaseFacade? openedBy = null;
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            openedBy = db.Database;
        }

        if (connection is not SqliteConnection sqlite)
        {
            return new WriteTransaction(
                null,
                null,
                await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false),
                openedBy);
        }

        // deferred: false is BEGIN IMMEDIATE. Serializable is the only isolation level SQLite has,
        // and the driver offers the deferred choice on the synchronous overload only - which costs
        // nothing here, since beginning a transaction is one small statement on an open connection.
        var immediate = sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Enlisted so the movements, the rebuilds and the batch row are written inside this
        // transaction rather than each opening one of their own - which SQLite would refuse, and
        // which would put the deductions outside the lock the stock was checked under.
        var enlisted = await db.Database
            .UseTransactionAsync(immediate, cancellationToken)
            .ConfigureAwait(false);

        return new WriteTransaction(immediate, enlisted, null, openedBy);
    }

    /// <summary>
    /// Whether the exception is the database refusing a second refill under one business's
    /// idempotency key. Any other database failure is left untranslated, because translating one
    /// would publish an internal message as a caller-facing conflict.
    /// </summary>
    public static bool IsDuplicateIdempotencyKey(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqliteException sqlite || sqlite.SqliteErrorCode != SqliteConstraintViolation)
            {
                continue;
            }

            if (IdempotencyKeyIndexColumns.All(column =>
                sqlite.Message.Contains(column, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The transaction handed back to the use case. It holds the provider transaction it owns and,
    /// separately, the EF wrapper that enlisted it: EF's wrapper is not owned (it came from
    /// <c>UseTransaction</c>), so disposing it only detaches the transaction from the context, and
    /// the rollback-on-dispose a refused refill relies on has to come from disposing the provider
    /// transaction itself.
    /// </summary>
    private sealed class WriteTransaction(
        SqliteTransaction? immediate,
        IDbContextTransaction? enlisted,
        IDbContextTransaction? owned,
        DatabaseFacade? openedConnection) : IPickListRefillWriteTransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (immediate is not null)
            {
                await immediate.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (enlisted is not null)
            {
                await enlisted.DisposeAsync().ConfigureAwait(false);
            }

            if (immediate is not null)
            {
                // Rolls back when CommitAsync was not reached, which is how a refused allocation
                // discards every movement the batch had already staged.
                await immediate.DisposeAsync().ConfigureAwait(false);
            }

            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            if (openedConnection is not null)
            {
                await openedConnection.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
