using System.Data;
using Inventory.Application.Nayax;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxConnectionSaveTransactions"/> (issue #537).
///
/// <para>It opens the transaction on the request's own <see cref="AppDbContext"/>, which is the
/// scoped unit of work both Nayax connection adapters are constructed with. That is what makes the
/// draft claim and the credential write one change: each of those adapters starts a transaction only
/// when there is none in progress and joins the ambient one when there is - the same shape the
/// human-run <c>migrate-nayax-connection</c> command relies on.</para>
///
/// <para><strong>It asks SQLite for <c>BEGIN IMMEDIATE</c>, like
/// <see cref="EfBusinessMembershipWriteStore"/> and for the same reason.</strong> A deferred
/// transaction - the default - takes its write lock at the first write, which is <em>after</em> the
/// read the claim is decided on: two simultaneous saves would both read the draft, and the one that
/// lost would meet the conflict at its own <c>DELETE</c>, where SQLite can only answer "busy"
/// against a transaction that is itself waiting to commit. Taking the write lock when the
/// transaction opens makes the second caller wait (within <c>Default Timeout</c>) and then read the
/// first one's committed state - no draft - so the refusal is the deliberate <c>409</c> rather than a
/// lock error. The conditional delete's own row count still guards the claim; this is what keeps the
/// ordinary path deterministic.</para>
///
/// <para>A non-SQLite relational provider falls back to its default transaction - still one
/// transaction around the claim and the write - and the EF InMemory provider some tests use has no
/// transaction at all, exactly as the other stores in this folder treat it. Neither fallback is a
/// supported production configuration.</para>
/// </summary>
public sealed class EfNayaxConnectionSaveTransactions : INayaxConnectionSaveTransactions
{
    private readonly AppDbContext _db;

    public EfNayaxConnectionSaveTransactions(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<INayaxConnectionSaveTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            // Nothing to begin, or already inside somebody else's transaction: that caller owns the
            // commit, and starting a second one here would either fail or commit half of it.
            return new SaveTransaction(null, null, null, null);
        }

        var connection = _db.Database.GetDbConnection();

        // Only closed if this transaction opened it: a test that shares one open connection across
        // contexts, and a request that already has one, must keep theirs.
        DatabaseFacade? openedBy = null;
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            openedBy = _db.Database;
        }

        if (connection is not SqliteConnection sqlite)
        {
            return new SaveTransaction(
                null,
                null,
                await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false),
                openedBy);
        }

        // deferred: false is BEGIN IMMEDIATE. Serializable is the only isolation level SQLite has,
        // and the driver offers the deferred choice on the synchronous overload only - which costs
        // nothing here, since beginning a transaction is one small statement on an open connection.
        var immediate = sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Enlisted so the stores' own statements run inside this transaction rather than opening one
        // of their own - which SQLite would refuse, and which would put the credential write outside
        // the lock the draft was claimed under.
        var enlisted = await _db.Database
            .UseTransactionAsync(immediate, cancellationToken)
            .ConfigureAwait(false);

        return new SaveTransaction(immediate, enlisted, null, openedBy);
    }

    private sealed class SaveTransaction(
        SqliteTransaction? immediate,
        IDbContextTransaction? enlisted,
        IDbContextTransaction? owned,
        DatabaseFacade? openedConnection) : INayaxConnectionSaveTransaction
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
                // Rolls back when CommitAsync was not reached, which is how a refused save discards
                // the claim and leaves the draft in place.
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
