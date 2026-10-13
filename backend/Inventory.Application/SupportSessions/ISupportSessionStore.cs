using Inventory.Domain.Tenancy;

namespace Inventory.Application.SupportSessions;

/// <summary>
/// Narrow persistence port for the server-side support sessions of issue #550 - the whole of what
/// this feature may reach in the database, and nothing else.
///
/// <para><strong>Nothing a request supplies can choose a row here.</strong> A session is found by the
/// authenticated actor it belongs to, or by the business a committed deactivation has just read
/// inside its own transaction. There is deliberately no lookup by key, by business the caller named,
/// or by the presented session id: the id-to-session lookup a support request needs is issue #551's,
/// and it arrives with that slice rather than being available to this one.</para>
///
/// <para><strong>The table is structurally non-tenant-owned</strong> (docs/architecture.md § Tenant
/// ownership), for the reason <c>MemberInvite</c> is: every request that reaches it carries a
/// <em>denied</em> business scope - the platform administrator is a member of no business - so a
/// central query filter keyed to a resolved business would match no row, and the ownership stamp on
/// <c>SaveChanges</c> has no business to stamp. The boundary for this table is therefore the business
/// id named in the statement, which only ever comes from the server's own side of the operation, and
/// the actor pair beside it.</para>
///
/// <para><strong>Both ends return the rows they closed, and both are called under the write
/// lock.</strong> Each reads the sessions that are active at the instant it was given and then makes
/// one conditional statement over exactly that condition, so the comparison and the write cannot be
/// separated by another writer: the caller holds issue #522's <c>BEGIN IMMEDIATE</c> transaction, so
/// no other connection can end, start or expire a row in between. The rows are returned because
/// every end is audited individually - whose access stopped, which business, and when - and an audit
/// trail built from a predicted result rather than the read one would be a trail of what the code
/// expected.</para>
/// </summary>
public interface ISupportSessionStore
{
    /// <summary>
    /// The one support session of <paramref name="actor"/> that is active at
    /// <paramref name="nowUtc"/>, or <see langword="null"/> when they have none.
    ///
    /// <para>"Active" is <see cref="SupportSessionPolicy.IsActive"/>: not ended, and not past its
    /// expiry. One active session per administrator is the contract the start enforces, and this is
    /// the read it enforces it with - so it answers a single session rather than a list.</para>
    /// </summary>
    Task<StoredSupportSession?> FindActiveForActorAsync(
        ActorIdentity actor,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores one new session and returns it as persisted, including the key the database assigned.
    /// </summary>
    Task<StoredSupportSession> StartAsync(NewSupportSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Ends every session of <paramref name="actor"/> that is still active at
    /// <paramref name="endedAtUtc"/>, and returns them as the end left them.
    ///
    /// <para>It is phrased as "every" rather than "the one" on purpose. Under the one-active-session
    /// rule it closes exactly one, but a row written by hand - or by a deployment that lost the rule
    /// - must not be able to leave an administrator unable to close their own access. An already
    /// ended or expired session is untouched: rewriting its instant would claim a transition that
    /// never happened, and leaving it alone is what makes a repeated end idempotent.</para>
    /// </summary>
    Task<IReadOnlyList<StoredSupportSession>> EndAllForActorAsync(
        ActorIdentity actor,
        DateTime endedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends every session of <paramref name="businessId"/> that is still active at
    /// <paramref name="endedAtUtc"/>, and returns them as the end left them.
    ///
    /// <para>It exists for the business deactivation of issue #545, which must leave no readable
    /// path into the business behind: a session that survived the business being switched off would
    /// be exactly the access the deactivation exists to stop, and a session ended by a deactivation
    /// that then rolled back would have been closed for nothing. The deactivation therefore calls it
    /// inside its own transaction, through <c>IBusinessDeactivationHook</c>, and a reactivation never
    /// revives what this closed.</para>
    /// </summary>
    Task<IReadOnlyList<StoredSupportSession>> EndAllForBusinessAsync(
        BusinessId businessId,
        DateTime endedAtUtc,
        CancellationToken cancellationToken);
}
