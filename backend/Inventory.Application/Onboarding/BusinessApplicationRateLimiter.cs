using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Onboarding;

/// <summary>
/// "At most <see cref="MaxApplicationsPerWindow"/> business applications per identity per
/// <see cref="Window"/>" (issue #541), claimed before the write transaction is opened.
///
/// <para><strong>Why the identity is the bucket.</strong> There is no business to rate-limit by -
/// the whole point of the endpoint is that the caller belongs to nowhere - and the identity is the
/// only thing a request cannot choose: it is the validated Entra <c>(tid, oid)</c> pair, not an
/// address, a name or a header. What the limit protects is the write path, which creates a tenant
/// row and a membership row inside the shared <c>BEGIN IMMEDIATE</c> transaction every membership
/// change serialises on, so a form submitted in a loop would otherwise take that write lock over
/// and over.</para>
///
/// <para>It is a sliding window rather than a fixed one, for the reason
/// <c>NayaxConnectionTestRateLimiter</c> is: a fixed bucket allows the full allowance at the end of
/// one window and the same again at the start of the next, which is twice the intended rate at the
/// moment it matters most. And it is kept in memory rather than in a column, like that limiter and
/// <c>MemberProfileRefreshThrottle</c>: what belongs in the database is the application, not how
/// often somebody pressed the button, and a restart costing one person one extra allowance is a far
/// better trade than a table and a write per attempt. On a single instance
/// (docs/architecture.md § SQLite operating assumptions) in-process state is the whole truth; a
/// second instance would mean this allowance per instance, which is still bounded.</para>
///
/// <para><strong>One lock over the whole table, deliberately.</strong> Expiring the attempts that
/// have left the window, deciding on the count, recording the claim and clearing out the identities
/// with nothing left are one indivisible operation, which is what makes a granted claim always a
/// counted claim. A per-identity lock would buy concurrency this endpoint has no use for - an
/// application is something a person does once - at the cost of the re-check dance a two-level
/// scheme needs to avoid counting a claim into a bucket that has just been forgotten.</para>
/// </summary>
public sealed class BusinessApplicationRateLimiter
{
    /// <summary>How many applications one identity may submit inside <see cref="Window"/>.</summary>
    public const int MaxApplicationsPerWindow = 5;

    /// <summary>
    /// How many identities may be remembered before those whose windows have fully passed are
    /// cleared out. It is a hint about memory, never a cap: crossing it expires attempts that have
    /// left the window and forgets the identities left with none, and it neither grants, denies nor
    /// resets an allowance.
    /// </summary>
    public const int MaxTrackedIdentities = 1024;

    /// <summary>The sliding window the attempt count is measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Dictionary<ActorIdentity, Queue<DateTime>> _attemptsByIdentity = [];
    private readonly object _gate = new();
    private readonly IClock _clock;

    public BusinessApplicationRateLimiter(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>How many identities are currently remembered.</summary>
    public int TrackedIdentities
    {
        get
        {
            lock (_gate)
            {
                return _attemptsByIdentity.Count;
            }
        }
    }

    /// <summary>
    /// Claims one application attempt for <paramref name="identity"/> and answers whether this
    /// caller got it.
    ///
    /// The claim is taken <em>before</em> the write, not after it, which is what makes several
    /// simultaneous submissions cost one allowance each rather than all passing a count nothing has
    /// incremented yet. An attempt that is then refused - by the eligibility rule, or by the write
    /// transaction - is not refunded: a refused attempt is exactly the one that gets repeated, and
    /// it costs the write lock the same as a successful one.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the application may proceed; otherwise <see langword="false"/>
    /// and <paramref name="retryAfter"/> is how long until the oldest attempt leaves the window.
    /// </returns>
    public bool TryClaim(ActorIdentity identity, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (!_attemptsByIdentity.TryGetValue(identity, out var attempts))
            {
                attempts = new Queue<DateTime>();
                _attemptsByIdentity[identity] = attempts;
            }

            ExpirePassedAttempts(attempts, now);

            if (attempts.Count >= MaxApplicationsPerWindow)
            {
                // The oldest attempt is the one whose departure frees a slot. Never negative: it is
                // still inside the window, or the expiry above would have removed it.
                retryAfter = Window - (now - attempts.Peek());
                return false;
            }

            attempts.Enqueue(now);
            PruneIdle(now);
        }

        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>
    /// Drops the attempts that have left the window, oldest first. A queue is in claim order, so
    /// the first attempt still inside the window ends it. A clock that moved backwards makes the
    /// elapsed time negative, which is never "the window has passed", so nothing is dropped.
    /// </summary>
    private static void ExpirePassedAttempts(Queue<DateTime> attempts, DateTime now)
    {
        while (attempts.Count > 0 && now - attempts.Peek() >= Window)
        {
            attempts.Dequeue();
        }
    }

    /// <summary>
    /// Expires the attempts that have left the window and forgets the identities left with none,
    /// once more than <see cref="MaxTrackedIdentities"/> are remembered.
    ///
    /// An identity is forgotten only when nothing it did is still inside the window, so clearing
    /// out can neither grant nor deny an attempt: dropping a queue that still held a live attempt
    /// would reset that identity's history and hand it the whole allowance again inside the same
    /// window. Called with <see cref="_gate"/> held, like everything else that touches the table.
    /// </summary>
    private void PruneIdle(DateTime now)
    {
        if (_attemptsByIdentity.Count <= MaxTrackedIdentities)
        {
            return;
        }

        foreach (var entry in _attemptsByIdentity.ToList())
        {
            ExpirePassedAttempts(entry.Value, now);

            if (entry.Value.Count == 0)
            {
                _attemptsByIdentity.Remove(entry.Key);
            }
        }
    }
}
