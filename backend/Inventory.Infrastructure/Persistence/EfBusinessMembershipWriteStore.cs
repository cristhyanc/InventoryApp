using System.Data;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessMembershipWriteStore"/> (issue #522). Like
/// every other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the
/// persistence models it reads.
///
/// It is the one place the <c>BEGIN IMMEDIATE</c> half of the shared membership write path is
/// implemented, and the only place in the application that asks SQLite for a transaction mode. The
/// reason it has to be explicit is that a deferred transaction - the default - takes its write lock
/// at the first write, which is *after* the eligibility check it is supposed to protect: two
/// callers would both read "no active membership", and only the second would find out at its
/// write. <c>BEGIN IMMEDIATE</c> takes the write lock when the transaction opens, so the second
/// caller waits (within <c>Default Timeout</c>) and then reads the first one's committed state.
///
/// SQLite is the database this application runs on (docs/architecture.md § SQLite operating
/// assumptions), so the mode is requested through the provider's own connection rather than
/// through raw SQL. A different relational provider falls back to its default transaction - still
/// one transaction around the read, the rule and the write - and the EF InMemory provider some
/// tests use has no transaction at all, exactly as the other stores in this folder treat it.
/// Neither fallback is a supported production configuration.
/// </summary>
public sealed class EfBusinessMembershipWriteStore : IBusinessMembershipWriteStore
{
    /// <summary>
    /// The columns the filtered unique index of issue #522 covers, as SQLite names them in a
    /// constraint-violation message.
    /// </summary>
    private static readonly string[] ActiveMembershipIndexColumns =
        ["BusinessMemberships.DirectoryTenantId", "BusinessMemberships.ObjectId"];

    /// <summary>
    /// The column that tells the two unique indexes on this table apart: the per-(actor, business)
    /// index names it too, and that violation is a different fact - this person already has a row
    /// in this business - which must not be reported as "already a member of a business".
    /// </summary>
    private const string PerBusinessIndexColumn = "BusinessMemberships.BusinessId";

    private readonly AppDbContext _db;

    public EfBusinessMembershipWriteStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IBusinessMembershipWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            return new WriteTransaction(null, null, null, null);
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
            return new WriteTransaction(
                null,
                null,
                await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false),
                openedBy);
        }

        // deferred: false is BEGIN IMMEDIATE. Serializable is the only isolation level SQLite has,
        // and the driver offers the deferred choice on the synchronous overload only - which costs
        // nothing here, since beginning a transaction is one small statement on an open connection.
        var immediate = sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Enlisted so the caller's own SaveChangesAsync writes inside this transaction rather than
        // opening one of its own - which SQLite would refuse, and which would put the write
        // outside the lock the rules were checked under.
        var enlisted = await _db.Database
            .UseTransactionAsync(immediate, cancellationToken)
            .ConfigureAwait(false);

        return new WriteTransaction(immediate, enlisted, null, openedBy);
    }

    public async Task<IReadOnlyList<IdentityMembership>> FindIdentityMembershipsAsync(
        ActorIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Every business, every state, and no business-state filter: a membership in a Pending or
        // Deactivated business still occupies this identity's one active membership. BusinessMembership
        // carries no tenant query filter (it is what resolves the tenant boundary), so this read
        // sees the identity's rows whatever business the request itself resolved to.
        return await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.DirectoryTenantId == identity.DirectoryTenantId
                && membership.ObjectId == identity.ObjectId)
            .OrderBy(membership => membership.Id)
            .Select(membership => new IdentityMembership(membership.Id, membership.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BusinessMembershipRole>> FindBusinessMembershipRolesAsync(
        BusinessId businessId,
        CancellationToken cancellationToken) =>
        await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.BusinessId == businessId.Value)
            .OrderBy(membership => membership.Id)
            // The stored role exactly as it is, including a value no BusinessRole declares:
            // BusinessOwnerRetention has to see it to refuse treating it as an Owner.
            .Select(membership => new BusinessMembershipRole(membership.Role, membership.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Recognises the filtered unique index refusing a second active membership, and nothing else.
    ///
    /// SQLite names the columns rather than the index in its message, and the two unique indexes on
    /// this table overlap on those columns, so the per-(actor, business) one is told apart by the
    /// <c>BusinessId</c> it also names. Any other database failure - including that one - is left
    /// untranslated rather than reported to a caller as a membership conflict.
    /// </summary>
    public bool IsDuplicateActiveMembership(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqliteException sqlite || sqlite.SqliteErrorCode != SqliteConstraintViolation)
            {
                continue;
            }

            var message = sqlite.Message;
            if (!message.Contains(PerBusinessIndexColumn, StringComparison.Ordinal)
                && ActiveMembershipIndexColumns.All(column =>
                    message.Contains(column, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// The transaction handed back to <c>MembershipWriteGuard</c>. It holds the provider
    /// transaction it owns and, separately, the EF wrapper that enlisted it: EF's wrapper is not
    /// owned (it came from <c>UseTransaction</c>), so disposing it only detaches the transaction
    /// from the context, and the rollback-on-dispose this path relies on has to come from
    /// disposing the provider transaction itself.
    /// </summary>
    private sealed class WriteTransaction(
        SqliteTransaction? immediate,
        IDbContextTransaction? enlisted,
        IDbContextTransaction? owned,
        DatabaseFacade? openedConnection) : IBusinessMembershipWriteTransaction
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
                // Rolls back when CommitAsync was not reached, which is how a refused rule
                // discards the caller's write.
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
