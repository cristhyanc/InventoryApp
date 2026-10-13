using System.Linq.Expressions;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISupportSessionStore"/> (issue #550). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence model it
/// reads and writes.
///
/// <para><strong>One table, and nothing else at all.</strong> This adapter touches
/// <c>SupportSessions</c> and no other table: not <c>Businesses</c>, not <c>BusinessMemberships</c>,
/// and nothing tenant-owned. The business's name a response reports comes from
/// <c>IPlatformBusinessDirectoryStore</c>, which is the one port a platform administrator reads
/// businesses through, so this slice adds no second cross-business read path. The request it serves
/// carries a <em>denied</em> <c>BusinessScope</c>, so the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> remain fully in force over everything else on the context.</para>
///
/// <para><strong>Why the identifying values are in every statement.</strong>
/// <c>SupportSession</c> is one of the five structural non-tenant-owned tables
/// (docs/architecture.md § Tenant ownership): a platform-admin request resolves no business, so a
/// central query filter keyed to a resolved business can never match these rows and the ownership
/// stamp has no business to stamp. The boundary for this table is therefore the actor pair, or the
/// business id, named in the <c>WHERE</c> clause - and both only ever come from the server's own side
/// of the operation: the validated token's <c>(tid, oid)</c>, or the business row the caller's
/// transaction read.</para>
///
/// <para><strong>Both ends are a read and one conditional statement, under the caller's write
/// lock.</strong> Each reads the sessions that are active at the instant it was given and then
/// updates exactly that condition, naming only the one column an end changes. The caller holds issue
/// #522's <c>BEGIN IMMEDIATE</c> transaction, so no other connection can start, end or alter a row
/// between the two: that is what makes the rows this returns the rows that were actually closed, which
/// is what the audit trail is built from. The <c>WHERE</c> clause still carries the full "only while
/// this session is active" condition rather than a list of keys, so even a stale read could not
/// reopen or re-stamp a session that was already closed.</para>
///
/// <para>The session id's plaintext never reaches this adapter: the port carries
/// <see cref="NewSupportSession.SessionIdHash"/>, which is all a row holds, and no read below projects
/// even that.</para>
/// </summary>
public sealed class EfSupportSessionStore : ISupportSessionStore
{
    /// <summary>
    /// The one projection every read uses, so a column added to the entity cannot reach one read and
    /// not another. It is an expression rather than a method so EF translates it into the
    /// <c>SELECT</c> list, and it deliberately omits <c>SessionIdHash</c>: reading sessions must never
    /// load the one value that has to stay in the database.
    /// </summary>
    private static readonly Expression<Func<Models.SupportSession, StoredSupportSession>> Projection =
        session => new StoredSupportSession(
            session.Id,
            session.BusinessId,
            session.ActorDirectoryTenantId,
            session.ActorObjectId,
            session.Reason,
            session.StartedAtUtc,
            session.ExpiresAtUtc,
            session.EndedAtUtc);

    private readonly AppDbContext _db;

    public EfSupportSessionStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public Task<StoredSupportSession?> FindActiveForActorAsync(
        ActorIdentity actor,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return ActiveOf(actor, nowUtc)
            .AsNoTracking()
            // Newest first, which under the one-active-session rule is the only one. A second row
            // written by hand is not a reason to fail the read: an administrator must always be able
            // to see, and end, the access they hold.
            .OrderByDescending(session => session.Id)
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<StoredSupportSession?> FindActiveForActorByIdHashAsync(
        ActorIdentity actor,
        string sessionIdHash,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdHash);

        // The actor pair and the hash are both in the WHERE clause, so a presented id can only ever
        // match a session of the caller's own - and the "still active" condition is in there too,
        // rather than checked on the result, so an ended or expired row is not something this can
        // hand back. The hash column is uniquely indexed, so this is one index seek.
        return ActiveOf(actor, nowUtc)
            .AsNoTracking()
            .Where(session => session.SessionIdHash == sessionIdHash)
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StoredSupportSession> StartAsync(
        NewSupportSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // The business is written here rather than stamped by BusinessOwnershipEnforcer, which does
        // not see this table. The value is the one the caller's own transaction read from the
        // business row; no request value can reach it, and nothing ever updates the column
        // afterwards.
        var row = new Models.SupportSession
        {
            BusinessId = session.BusinessId,
            SessionIdHash = session.SessionIdHash,
            ActorDirectoryTenantId = session.Actor.DirectoryTenantId,
            ActorObjectId = session.Actor.ObjectId,
            Reason = session.Reason,
            StartedAtUtc = session.StartedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc,
        };

        _db.SupportSessions.Add(row);

        // Saved before returning, inside the caller's transaction, because the caller's own next
        // statement - and the rule re-checks around it - have to see this row.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StoredSupportSession(
            row.Id,
            row.BusinessId,
            row.ActorDirectoryTenantId,
            row.ActorObjectId,
            row.Reason,
            row.StartedAtUtc,
            row.ExpiresAtUtc,
            row.EndedAtUtc);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredSupportSession>> EndAllForActorAsync(
        ActorIdentity actor,
        DateTime endedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return EndAsync(ActiveOf(actor, endedAtUtc), endedAtUtc, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredSupportSession>> EndAllForBusinessAsync(
        BusinessId businessId,
        DateTime endedAtUtc,
        CancellationToken cancellationToken) =>
        EndAsync(
            Active(endedAtUtc).Where(session => session.BusinessId == businessId.Value),
            endedAtUtc,
            cancellationToken);

    /// <summary>
    /// Closes whichever sessions <paramref name="sessions"/> selects and answers with them as the end
    /// left them.
    ///
    /// <para>The read comes first so the audit trail describes rows that exist, and the write is one
    /// conditional <c>ExecuteUpdate</c> over the same predicate - naming only <c>EndedAtUtc</c>, the
    /// single column an end changes. Under the caller's <c>BEGIN IMMEDIATE</c> transaction nothing can
    /// land in between, so the two are one operation; without that lock the <c>WHERE</c> clause would
    /// still refuse to touch a session that had since been closed.</para>
    /// </summary>
    private async Task<IReadOnlyList<StoredSupportSession>> EndAsync(
        IQueryable<Models.SupportSession> sessions,
        DateTime endedAtUtc,
        CancellationToken cancellationToken)
    {
        var ending = await sessions
            .AsNoTracking()
            .OrderBy(session => session.Id)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ending.Count == 0)
        {
            // Nothing is active, so there is nothing to write. Returning early keeps a repeated end
            // from issuing a statement that could only ever match no row.
            return ending;
        }

        await sessions
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.EndedAtUtc, endedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return [.. ending.Select(session => session with { EndedAtUtc = endedAtUtc })];
    }

    /// <summary>
    /// The sessions of one administrator that are active at <paramref name="nowUtc"/>. Every
    /// statement about "my session" starts here, which is what makes the actor impossible to omit on
    /// one method.
    /// </summary>
    private IQueryable<Models.SupportSession> ActiveOf(ActorIdentity actor, DateTime nowUtc) =>
        Active(nowUtc).Where(session =>
            session.ActorDirectoryTenantId == actor.DirectoryTenantId
            && session.ActorObjectId == actor.ObjectId);

    /// <summary>
    /// The sessions that are still active at <paramref name="nowUtc"/>: not ended, and not past their
    /// expiry. It is the SQL half of <see cref="SupportSessionPolicy.IsActive"/> - the same two
    /// conditions, in a <c>WHERE</c> clause so a check and a write cannot be separated.
    /// </summary>
    private IQueryable<Models.SupportSession> Active(DateTime nowUtc) =>
        _db.SupportSessions.Where(session =>
            session.EndedAtUtc == null && session.ExpiresAtUtc > nowUtc);
}
