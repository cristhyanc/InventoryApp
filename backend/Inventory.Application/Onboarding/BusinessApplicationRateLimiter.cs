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
    ///
    /// <para>The one exception is an <em>identical resubmission</em>, which gives its claim back
    /// through <see cref="Release"/> (issue #681). That case exists precisely so a lost response
    /// cannot create a second business, and it writes nothing and opens no transaction - so charging
    /// an allowance for it meant an applicant whose connection dropped five times was locked out of
    /// an endpoint that had been answering "here is the application you already have" all along.
    /// </para>
    /// </summary>
    /// <param name="identity">Whose allowance to claim.</param>
    /// <param name="retryAfter">
    /// How long until the oldest attempt leaves the window, when the claim was refused.
    /// </param>
    /// <param name="claimedAtUtc">
    /// The instant this attempt was recorded at, which <see cref="Release"/> needs to give back
    /// <em>this</em> attempt. Meaningful only when the claim succeeded.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the application may proceed; otherwise <see langword="false"/>
    /// and <paramref name="retryAfter"/> is how long until the oldest attempt leaves the window.
    /// </returns>
    public bool TryClaim(ActorIdentity identity, out TimeSpan retryAfter, out DateTime claimedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var now = _clock.UtcNow;
        claimedAtUtc = now;

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
    /// The same claim for a caller that will not give it back, so it has no use for the instant.
    /// </summary>
    /// <param name="identity">Whose allowance to claim.</param>
    /// <param name="retryAfter">
    /// How long until the oldest attempt leaves the window, when the claim was refused.
    /// </param>
    public bool TryClaim(ActorIdentity identity, out TimeSpan retryAfter) =>
        TryClaim(identity, out retryAfter, out _);

    /// <summary>
    /// Gives back an attempt that turned out to cost nothing (issue #681).
    ///
    /// <para>It removes <strong>one</strong> recorded attempt at <paramref name="claimedAtUtc"/>, and
    /// only if one is still there: an attempt that has since left the window is already gone, and a
    /// release can never remove more than it was given. Several attempts recorded at the very same
    /// instant - the clock is a fake one in tests, and a real one has limited resolution - are
    /// distinct allowances, so exactly one of them is returned.</para>
    ///
    /// <para>The queue is rebuilt rather than dequeued, because the attempt being given back is not
    /// necessarily the oldest one: another submission may have been recorded while this one was
    /// reading. Rebuilding keeps the remaining attempts in claim order, which is what
    /// <see cref="ExpirePassedAttempts"/> and the <c>retryAfter</c> calculation both rely on.</para>
    /// </summary>
    /// <param name="identity">Whose allowance to give back.</param>
    /// <param name="claimedAtUtc">
    /// The instant <see cref="TryClaim(ActorIdentity, out TimeSpan, out DateTime)"/> reported for the
    /// attempt being returned.
    /// </param>
    /// <returns><c>true</c> when an attempt was found and given back.</returns>
    public bool Release(ActorIdentity identity, DateTime claimedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (!_attemptsByIdentity.TryGetValue(identity, out var attempts))
            {
                return false;
            }

            var remaining = new Queue<DateTime>(attempts.Count);
            var released = false;

            foreach (var attempt in attempts)
            {
                if (!released && attempt == claimedAtUtc)
                {
                    released = true;
                    continue;
                }

                remaining.Enqueue(attempt);
            }

            if (!released)
            {
                return false;
            }

            if (remaining.Count == 0)
            {
                _attemptsByIdentity.Remove(identity);
            }
            else
            {
                _attemptsByIdentity[identity] = remaining;
            }

            return true;
        }
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
