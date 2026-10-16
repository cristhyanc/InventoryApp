using System.Collections.Concurrent;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The per-business sliding-window allowance that <c>NayaxConnectionTestRateLimiter</c> (issues
/// #536 and #606) and <c>NayaxSalesSyncRateLimiter</c> (issue #681) each wrap with their own limit,
/// window and reasons. It is one mechanism written once, so the concurrency rules issue #606 fixed
/// cannot drift apart between two copies.
///
/// <para>A claim is taken before the work it guards, so simultaneous callers cost one allowance each
/// rather than all passing a count nothing has incremented yet, and a run that then fails is not
/// refunded. Expiring the runs that have left the window, deciding on the count and recording the
/// claim are one operation on the history the business is tracked by, so a claim that is granted is
/// always counted - including when clearing out forgets the business in between (issue #606).</para>
///
/// <para>It is a sliding window rather than a fixed one, because a fixed bucket allows the full
/// allowance at the end of one bucket and again at the start of the next. The window is kept in
/// memory: on a single instance (docs/architecture.md § SQLite operating assumptions) in-process
/// state is the whole truth, and a restart costs a business at most one extra allowance.</para>
/// </summary>
internal sealed class BusinessSlidingWindowLimiter
{
    private readonly ConcurrentDictionary<BusinessId, Queue<DateTime>> _runsByBusiness = new();
    private readonly IClock _clock;
    private readonly int _maxRunsPerWindow;
    private readonly TimeSpan _window;
    private readonly int _maxTrackedBusinesses;

    public BusinessSlidingWindowLimiter(IClock clock, int maxRunsPerWindow, TimeSpan window, int maxTrackedBusinesses)
    {
        _clock = clock;
        _maxRunsPerWindow = maxRunsPerWindow;
        _window = window;
        _maxTrackedBusinesses = maxTrackedBusinesses;
    }

    /// <summary>How many businesses are currently remembered.</summary>
    public int TrackedBusinesses => _runsByBusiness.Count;

    /// <summary>
    /// Claims one run for <paramref name="business"/> and answers whether this caller got it.
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
            // simultaneous claims racing on the same queue is the case a limit exists for.
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

                if (runs.Count >= _maxRunsPerWindow)
                {
                    // The oldest run is the one whose departure frees a slot. Never negative: it is
                    // still inside the window, or the expiry above would have removed it.
                    retryAfter = _window - (now - runs.Peek());
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
    private void ExpirePassedRuns(Queue<DateTime> runs, DateTime now)
    {
        while (runs.Count > 0 && now - runs.Peek() >= _window)
        {
            runs.Dequeue();
        }
    }

    /// <summary>
    /// Expires the runs that have left the window and forgets the businesses left with none, once
    /// more than the tracked-business hint are remembered.
    ///
    /// <para>A business is forgotten only when nothing it did is still inside the window, so
    /// clearing out can neither grant nor deny a run: dropping a queue that still held a live run
    /// would reset that business's history and let it start the window again (issue #606).
    /// Expiring the passed runs of a business that stays tracked is the same bookkeeping a claim
    /// does, and changes no answer.</para>
    /// </summary>
    private void PruneIdle(DateTime now)
    {
        if (_runsByBusiness.Count <= _maxTrackedBusinesses)
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
