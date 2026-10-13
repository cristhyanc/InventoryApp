using Inventory.Application.SupportSessions;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.SupportSessions;

/// <summary>
/// In-memory fake of the support-session port (issue #550).
///
/// <para>It reports every call to an observer, because the order is what the use cases have to get
/// right: the "do I already have one?" read happens <em>inside</em> the write transaction, and the
/// deactivation's end happens inside the deactivation's. A fake that only answered reads could not
/// tell a correct start from one that decided on the view it was handed before the write lock was
/// taken.</para>
///
/// <para>Both ends reproduce the one property that matters about the real statements: they close only
/// the sessions that are active at the instant they were given - <see cref="SupportSessionPolicy"/>'s
/// own rule, not a stored flag - and they answer with the rows they closed, so a test can assert what
/// was audited against what was written.</para>
/// </summary>
public sealed class FakeSupportSessionStore : ISupportSessionStore
{
    public const string ActiveRead = "support-session-active-read";
    public const string IdHashRead = "support-session-id-hash-read";
    public const string Start = "support-session-start";
    public const string EndForActor = "support-session-end-for-actor";
    public const string EndForBusiness = "support-session-end-for-business";

    private readonly Dictionary<int, StoredSupportSession> _rows = [];

    /// <summary>
    /// Each row's stored session-id hash, which <see cref="StoredSupportSession"/> deliberately does
    /// not carry: the real adapter keeps that column out of every projection and only ever names it
    /// in a <c>WHERE</c> clause, which is exactly what the lookup below reproduces.
    /// </summary>
    private readonly Dictionary<int, string> _idHashes = [];

    private int _nextKey = 1;

    /// <summary>Where every call is reported, so one test can see the whole order.</summary>
    public Action<string>? Observer { get; set; }

    /// <summary>The session-id hash the last started session was stored with.</summary>
    public string? LastSessionIdHash { get; private set; }

    /// <summary>The actor the last active-session read asked about.</summary>
    public ActorIdentity? LastQueriedActor { get; private set; }

    /// <summary>Every row as it stands now, oldest first.</summary>
    public IReadOnlyList<StoredSupportSession> Rows => [.. _rows.Values.OrderBy(row => row.Key)];

    /// <summary>The row as it stands now, for asserting what a write did or did not do.</summary>
    public StoredSupportSession Row(int key) => _rows[key];

    /// <summary>
    /// Seeds one session without going through the write path - another request's committed session,
    /// or a row written by hand. Deliberately unobserved: it is not something the use case did.
    /// </summary>
    public int Add(
        int businessId,
        ActorIdentity actor,
        DateTime startedAtUtc,
        DateTime expiresAtUtc,
        DateTime? endedAtUtc = null,
        string reason = "Investigating a reported error",
        string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var stored = new StoredSupportSession(
            _nextKey++,
            businessId,
            actor.DirectoryTenantId,
            actor.ObjectId,
            reason,
            startedAtUtc,
            expiresAtUtc,
            endedAtUtc);

        _rows[stored.Key] = stored;
        if (sessionId is not null)
        {
            _idHashes[stored.Key] = SupportSessionId.Hash(sessionId);
        }

        return stored.Key;
    }

    public Task<StoredSupportSession?> FindActiveForActorByIdHashAsync(
        ActorIdentity actor,
        string sessionIdHash,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdHash);
        Observer?.Invoke(IdHashRead);
        LastQueriedActor = actor;

        // The actor, the hash and "still active" are all conditions of the match, exactly as they
        // are in the adapter's WHERE clause - so a leaked id matches nothing for anybody else.
        return Task.FromResult(ActiveOf(actor, nowUtc)
            .Where(row =>
                _idHashes.TryGetValue(row.Key, out var hash)
                && string.Equals(hash, sessionIdHash, StringComparison.Ordinal))
            .OrderByDescending(row => row.Key)
            .FirstOrDefault());
    }

    public Task<StoredSupportSession?> FindActiveForActorAsync(
        ActorIdentity actor,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Observer?.Invoke(ActiveRead);
        LastQueriedActor = actor;

        return Task.FromResult(ActiveOf(actor, nowUtc).OrderByDescending(row => row.Key).FirstOrDefault());
    }

    public Task<StoredSupportSession> StartAsync(
        NewSupportSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        Observer?.Invoke(Start);
        LastSessionIdHash = session.SessionIdHash;

        var stored = new StoredSupportSession(
            _nextKey++,
            session.BusinessId,
            session.Actor.DirectoryTenantId,
            session.Actor.ObjectId,
            session.Reason,
            session.StartedAtUtc,
            session.ExpiresAtUtc,
            EndedAtUtc: null);

        _rows[stored.Key] = stored;
        _idHashes[stored.Key] = session.SessionIdHash;
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<StoredSupportSession>> EndAllForActorAsync(
        ActorIdentity actor,
        DateTime endedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Observer?.Invoke(EndForActor);

        return End(ActiveOf(actor, endedAtUtc), endedAtUtc);
    }

    public Task<IReadOnlyList<StoredSupportSession>> EndAllForBusinessAsync(
        BusinessId businessId,
        DateTime endedAtUtc,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(EndForBusiness);

        return End(
            Active(endedAtUtc).Where(row => row.BusinessId == businessId.Value),
            endedAtUtc);
    }

    private IEnumerable<StoredSupportSession> ActiveOf(ActorIdentity actor, DateTime nowUtc) =>
        Active(nowUtc).Where(row =>
            string.Equals(row.ActorDirectoryTenantId, actor.DirectoryTenantId, StringComparison.Ordinal)
            && string.Equals(row.ActorObjectId, actor.ObjectId, StringComparison.Ordinal));

    private IEnumerable<StoredSupportSession> Active(DateTime nowUtc) =>
        _rows.Values.Where(row => SupportSessionPolicy.IsActive(row.ExpiresAtUtc, row.EndedAtUtc, nowUtc));

    private Task<IReadOnlyList<StoredSupportSession>> End(
        IEnumerable<StoredSupportSession> sessions,
        DateTime endedAtUtc)
    {
        var ended = sessions
            .OrderBy(row => row.Key)
            .Select(row => row with { EndedAtUtc = endedAtUtc })
            .ToList();

        foreach (var row in ended)
        {
            _rows[row.Key] = row;
        }

        return Task.FromResult<IReadOnlyList<StoredSupportSession>>(ended);
    }
}

/// <summary>
/// Collects the support-session audit events of issue #550, so a test can assert all three halves:
/// that a started session is recorded once with the actor, the business, the reason and both
/// instants; that every end is recorded with why it ended; and that a refused start records nothing.
/// </summary>
public sealed class FakeSupportSessionAudit : ISupportSessionAudit
{
    public const string Recorded = "support-session-audit";

    private readonly List<SupportSessionAuditEntry> _entries = [];

    public IReadOnlyList<SupportSessionAuditEntry> Entries => _entries;

    /// <summary>Where every call is reported, so one test can see the whole order.</summary>
    public Action<string>? Observer { get; set; }

    public void Record(SupportSessionAuditEntry entry)
    {
        Observer?.Invoke(Recorded);
        _entries.Add(entry);
    }
}
