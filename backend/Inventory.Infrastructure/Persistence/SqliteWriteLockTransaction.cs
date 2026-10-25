using System.Data;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// One <c>BEGIN IMMEDIATE</c> transaction over the request's own <see cref="AppDbContext"/>, as
/// <see cref="EfBusinessMembershipWriteStore"/> (issue #522), <see cref="EfNayaxConnectionSaveTransactions"/>
/// (issue #537) and <see cref="EfPickListRefillStore"/> (issue #743) each open one.
///
/// <para><strong>Why immediate.</strong> A deferred transaction - SQLite's default, and what
/// <c>BeginTransactionAsync</c> opens - takes its write lock at the first <em>write</em>, which is
/// after the reads the operation is decided on. Two operations then both read a machine nothing
/// refers to and nothing links, and the second one's write finds the row the first one deleted or
/// linked: with a read lock already held, SQLite cannot even make the second wait, so it answers
/// <c>SQLITE_BUSY</c> rather than serialising them. <c>BEGIN IMMEDIATE</c> takes the write lock when
/// the transaction opens, so the second caller waits (within <c>Default Timeout</c>) and then reads
/// the first one's committed state - which is what turns a race into a deliberate refusal instead of
/// a lock error.</para>
///
/// <para>It is shared by the two operations that have to be serialised <em>against each other</em>
/// (issue #896): deleting one of the business's own machines, and the provider machine review's
/// apply that may link the very same row. Taking the same lock is what makes "nothing refers to this
/// machine and no provider names it" true at the moment the row is removed.</para>
///
/// <para>A non-SQLite relational provider falls back to its default transaction - still one
/// transaction around the reads and the writes - and the EF InMemory provider some tests use has no
/// transaction at all, exactly as the other stores in this folder treat it. Neither fallback is a
/// supported production configuration (docs/architecture.md § SQLite operating assumptions). A
/// context that is already inside somebody else's transaction gets a no-op: that caller owns the
/// commit, and starting a second one here would either fail or commit half of it.</para>
/// </summary>
internal sealed class SqliteWriteLockTransaction : IAsyncDisposable
{
    private readonly SqliteTransaction? _immediate;
    private readonly IDbContextTransaction? _enlisted;
    private readonly IDbContextTransaction? _owned;
    private readonly DatabaseFacade? _openedConnection;

    private SqliteWriteLockTransaction(
        SqliteTransaction? immediate,
        IDbContextTransaction? enlisted,
        IDbContextTransaction? owned,
        DatabaseFacade? openedConnection)
    {
        _immediate = immediate;
        _enlisted = enlisted;
        _owned = owned;
        _openedConnection = openedConnection;
    }

    /// <summary>Opens the transaction, taking SQLite's write lock immediately where there is one.</summary>
    public static async Task<SqliteWriteLockTransaction> BeginAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return new SqliteWriteLockTransaction(null, null, null, null);

        var connection = db.Database.GetDbConnection();

        // Only closed again if this transaction opened it: a test that shares one open connection
        // across contexts, and a request that already has one, must keep theirs.
        DatabaseFacade? openedBy = null;
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            openedBy = db.Database;
        }

        if (connection is not SqliteConnection sqlite)
        {
            return new SqliteWriteLockTransaction(
                null,
                null,
                await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false),
                openedBy);
        }

        // deferred: false is BEGIN IMMEDIATE. Serializable is the only isolation level SQLite has,
        // and the driver offers the deferred choice on the synchronous overload only - which costs
        // nothing here, since beginning a transaction is one small statement on an open connection.
        var immediate = sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Enlisted so every read and write of the operation runs inside this transaction rather than
        // opening one of its own, which SQLite would refuse.
        var enlisted = await db.Database
            .UseTransactionAsync(immediate, cancellationToken)
            .ConfigureAwait(false);

        return new SqliteWriteLockTransaction(immediate, enlisted, null, openedBy);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_immediate is not null)
        {
            await _immediate.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_owned is not null)
        {
            await _owned.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_enlisted is not null)
        {
            // Not owned - it came from UseTransaction - so disposing it only detaches the
            // transaction from the context.
            await _enlisted.DisposeAsync().ConfigureAwait(false);
        }

        if (_immediate is not null)
        {
            // Rolls back when CommitAsync was not reached, which is how a refusal discards
            // everything the operation staged.
            await _immediate.DisposeAsync().ConfigureAwait(false);
        }

        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }

        if (_openedConnection is not null)
        {
            await _openedConnection.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
