using System.Collections.Concurrent;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Nayax;

/// <summary>
/// "At most <see cref="MaxRunsPerWindow"/> connection tests per business per
/// <see cref="Window"/>" (issue #536), enforced before any Nayax call is made.
///
/// <para>Since issue #537 the Owner-only re-test of the <em>stored</em> credentials claims from this
/// same limiter, so test and re-test runs count together: a re-test is the same eight calls against
/// the same upstream, and two separate allowances would simply double the rate.</para>
///
/// <para>The limit is per business rather than per actor because what it protects is the upstream:
/// one test is eight Nayax calls, and a form with a Test button is trivially clickable in a loop.
/// Rate-limiting the actor would let a second Owner start again from zero against the same operator
/// account, so the business - the owner of the operator account being tested - is the right
/// bucket.</para>
///
/// <para>It is a sliding window rather than a fixed one: a fixed ten-minute bucket allows
/// <see cref="MaxRunsPerWindow"/> runs at the end of one bucket and the same number at the start of
/// the next, which is twice the intended rate at the moment it matters most.</para>
///
/// <para>The window is kept in memory rather than in a column, like
/// <c>MemberProfileRefreshThrottle</c>: the record of a business's connection is what it holds, not
/// how often somebody pressed Test, and a restart costing a business one extra allowance is a far
/// better trade than a table and a write per attempt. On a single instance
/// (docs/architecture.md § SQLite operating assumptions) in-process state is the whole truth; a
/// second instance would mean this allowance per instance, which is still bounded.</para>
/// </summary>
public sealed class NayaxConnectionTestRateLimiter
{
    /// <summary>How many test runs one business may start inside <see cref="Window"/>.</summary>
    public const int MaxRunsPerWindow = 5;

    /// <summary>The sliding window the run count is measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many businesses may be remembered before the businesses whose windows have fully passed
    /// are cleared out. It is a hint about memory, never a cap: crossing it expires attempts that
    /// have left the window and forgets the businesses left with none, and it neither grants, denies
    /// nor resets an allowance (issue #606).
    /// </summary>
    public const int MaxTrackedBusinesses = 256;

    private readonly ConcurrentDictionary<BusinessId, Queue<DateTime>> _runsByBusiness = new();
    private readonly IClock _clock;

    public NayaxConnectionTestRateLimiter(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>How many businesses are currently remembered.</summary>
    public int TrackedBusinesses => _runsByBusiness.Count;

    /// <summary>
    /// Claims one run for <paramref name="business"/> and answers whether this caller got it.
    ///
    /// The claim is taken <em>before</em> the Nayax calls, not after them, which is what makes
    /// several simultaneous presses cost one allowance each rather than all passing a count that
    /// nothing has incremented yet. A run that then fails is not refunded: a failing test is exactly
    /// the run that gets repeated, and it costs the upstream the same eight calls as a successful
    /// one.
    ///
    /// <para>Expiring the runs that have left the window, deciding on the count and recording the
    /// claim are one operation on the history this business is tracked by, so a claim that is
    /// granted is always counted - including when clearing out forgets the business in between
    /// (issue #606).</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the run may proceed; otherwise <see langword="false"/> and
    /// <paramref name="retryAfter"/> is how long until the oldest run leaves the window.
    /// </returns>
    public bool TryClaim(BusinessId business, out TimeSpan retryAfter)
    {
        var now = _clock.UtcNow;

        while (true)
        {
            var runs = _runsByBusiness.GetOrAdd(business, static _ => new Queue<DateTime>());

            // One business's queue is mutated under its own lock. ConcurrentDictionary makes finding
            // the queue safe; it does nothing for the read-modify-write inside one, and two
            // simultaneous presses racing on the same queue is the case this limit exists for.
            lock (runs)
            {
                // Still the queue this business is tracked by? Clearing out (below) may have
                // forgotten it between the lookup above and this lock, and counting a run into a
                // queue nothing holds any more would lose it: the next claim would find a fresh
                // queue and hand the business one allowance more than the window allows. A removal
                // happens under this same lock, so re-reading here is conclusive for as long as it
                // is held - and losing the race simply means claiming against the live queue
                // instead (issue #606).
                if (!_runsByBusiness.TryGetValue(business, out var tracked) || !ReferenceEquals(tracked, runs))
                {
                    continue;
                }

                ExpirePassedRuns(runs, now);

                if (runs.Count >= MaxRunsPerWindow)
                {
                    // The oldest run is the one whose departure frees a slot. Never negative: it is
                    // still inside the window, or the expiry above would have removed it.
                    retryAfter = Window - (now - runs.Peek());
                    return false;
                }

                runs.Enqueue(now);
            }

            break;
        }

        PruneIdle(now);
        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>
    /// Drops the runs that have left the window, oldest first. A queue is in claim order, so the
    /// first run still inside the window ends it. A clock that moved backwards makes the elapsed
    /// time negative, which is never "the window has passed", so nothing is dropped.
    /// </summary>
    private static void ExpirePassedRuns(Queue<DateTime> runs, DateTime now)
    {
        while (runs.Count > 0 && now - runs.Peek() >= Window)
        {
            runs.Dequeue();
        }
    }

    /// <summary>
    /// Expires the runs that have left the window and forgets the businesses left with none, once
    /// more than <see cref="MaxTrackedBusinesses"/> are remembered.
    ///
    /// <para>A business is forgotten only when nothing it did is still inside the window, so
    /// clearing out can neither grant nor deny a run: dropping a queue that still held a live run
    /// would reset that business's history and let it start the window again, which is five more
    /// runs against the upstream inside the same ten minutes (issue #606). Expiring the passed runs
    /// of a business that stays tracked is the same bookkeeping a claim does, and changes no
    /// answer.</para>
    /// </summary>
    private void PruneIdle(DateTime now)
    {
        if (_runsByBusiness.Count <= MaxTrackedBusinesses)
        {
            return;
        }

        foreach (var entry in _runsByBusiness)
        {
            var runs = entry.Value;
            lock (runs)
            {
                ExpirePassedRuns(runs, now);

                if (runs.Count > 0)
                {
                    continue;
                }

                // Removed by key and value together, so a queue another caller replaced in the
                // meantime is left alone. A caller that looked this queue up and is now waiting on
                // its lock finds it no longer tracked and claims against the live queue instead,
                // so the run it is about to be granted is still counted.
                _runsByBusiness.TryRemove(entry);
            }
        }
    }
}
