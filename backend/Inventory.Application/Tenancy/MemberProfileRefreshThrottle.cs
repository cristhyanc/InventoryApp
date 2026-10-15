using System.Collections.Concurrent;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// "At most once per hour per membership" (issue #524), enforced where the refresh is scheduled so
/// that a request inside the hour queues nothing at all rather than queuing a write that turns out
/// to be unnecessary.
///
/// <para>The rule exists because every request a member makes carries the same two claims: a
/// member working in the application makes dozens a minute, and their display name changes perhaps
/// once in their employment. One refresh an hour is already far more current than the data needs to
/// be.</para>
///
/// <para>The slot belongs to an <see cref="ActorIdentity"/>, which - since issue #522 - has at most
/// one active membership, so "per identity" and "per membership" are the same set. It is kept in
/// memory rather than in a column: the two display columns say what is stored, not when it was last
/// checked, and a restart costing one extra refresh per member is a better trade than a third
/// column and a write on every request to maintain it. On a single instance (docs/architecture.md
/// § SQLite operating assumptions) in-process state is also the whole truth; a second instance
/// would mean at most one refresh per instance per hour, which the write path handles as the
/// no-op it is.</para>
/// </summary>
public sealed class MemberProfileRefreshThrottle
{
    /// <summary>How long a claimed refresh keeps the next one out.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// How many identities may be remembered before expired entries are cleared out.
    /// </summary>
    public const int MaxTrackedIdentities = 512;

    private readonly ConcurrentDictionary<ActorIdentity, DateTime> _lastClaimedUtc = new();
    private readonly IClock _clock;

    public MemberProfileRefreshThrottle(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>How many identities are currently remembered.</summary>
    public int TrackedIdentities => _lastClaimedUtc.Count;

    /// <summary>
    /// Claims the refresh slot for one identity, and answers whether this caller got it.
    ///
    /// The claim is made before the refresh is written, not after, and that is deliberate: it is
    /// what makes several simultaneous requests from one member produce one refresh rather than
    /// several. A refresh that then <em>fails</em> gives its claim back through
    /// <see cref="Release"/> (issue #681), so the member's next request can try again instead of
    /// their stored details staying stale for an hour because of a transient write failure.
    /// </summary>
    /// <param name="identity">Whose slot to claim.</param>
    /// <param name="claimedAtUtc">
    /// The instant this claim was recorded at, which <see cref="Release"/> needs in order to give
    /// back <em>this</em> claim and not a later one. Meaningful only when the claim succeeded.
    /// </param>
    public bool TryClaim(ActorIdentity identity, out DateTime claimedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var now = _clock.UtcNow;
        claimedAtUtc = now;

        // At most two iterations: a lost race means another request has just claimed the slot, and
        // the re-read then sees its instant and refuses. Compare-and-swap rather than AddOrUpdate,
        // whose factories may run more than once under contention - which would be a second claim
        // nobody made.
        while (true)
        {
            if (_lastClaimedUtc.TryGetValue(identity, out var lastClaimed))
            {
                // A clock that moved backwards leaves the slot closed rather than reopening it: the
                // fail-safe direction for this rule is not to refresh.
                if (now - lastClaimed < Interval)
                {
                    return false;
                }

                if (_lastClaimedUtc.TryUpdate(identity, now, lastClaimed))
                {
                    PruneExpired(now);
                    return true;
                }
            }
            else if (_lastClaimedUtc.TryAdd(identity, now))
            {
                PruneExpired(now);
                return true;
            }
        }
    }

    /// <summary>
    /// The same claim for a caller that will not give it back, so it has no use for the instant.
    /// </summary>
    /// <param name="identity">Whose slot to claim.</param>
    public bool TryClaim(ActorIdentity identity) => TryClaim(identity, out _);

    /// <summary>
    /// Gives back a claim whose refresh did not happen (issue #681), so the member's next request
    /// schedules another attempt instead of waiting out the hour.
    ///
    /// <para><strong>It gives back only the claim it was told about.</strong> The removal matches
    /// the identity <em>and</em> the instant <see cref="TryClaim(ActorIdentity, out DateTime)"/>
    /// recorded, so a release that
    /// arrives after a later request has already claimed the slot removes nothing and leaves that
    /// newer claim in force - which is what keeps "at most one refresh an hour" true even while a
    /// failed one is being given back.</para>
    ///
    /// <para>It is deliberately not called when the queue drops a refresh:
    /// <see cref="ScheduleMemberProfileRefresh"/> explains why a full queue is the one case where
    /// consuming the hour is the right answer. This is for a refresh that was accepted and then
    /// failed - a transient write failure, a cancelled drain - where nothing was written and nothing
    /// would be gained by keeping the slot shut.</para>
    /// </summary>
    /// <param name="identity">Whose slot to give back.</param>
    /// <param name="claimedAtUtc">
    /// The instant <see cref="TryClaim(ActorIdentity, out DateTime)"/> reported for that claim.
    /// </param>
    /// <returns><c>true</c> when the claim was still the current one and was given back.</returns>
    public bool Release(ActorIdentity identity, DateTime claimedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return _lastClaimedUtc.TryRemove(new KeyValuePair<ActorIdentity, DateTime>(identity, claimedAtUtc));
    }

    /// <summary>
    /// Forgets the identities whose hour has passed, once there are more of them than
    /// <see cref="MaxTrackedIdentities"/>.
    ///
    /// Only an identity that resolved an active membership ever reaches this throttle, so what it
    /// remembers is bounded by the people who signed in within the hour - a human-approved set. The
    /// clear-out is still here so a long-running process does not keep an entry per identity for
    /// ever, and it only removes entries that would allow a refresh anyway.
    /// </summary>
    private void PruneExpired(DateTime now)
    {
        if (_lastClaimedUtc.Count <= MaxTrackedIdentities)
        {
            return;
        }

        foreach (var entry in _lastClaimedUtc)
        {
            if (now - entry.Value >= Interval)
            {
                _lastClaimedUtc.TryRemove(entry);
            }
        }
    }
}
