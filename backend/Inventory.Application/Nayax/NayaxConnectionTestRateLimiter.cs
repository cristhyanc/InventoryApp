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
    /// How many businesses may be remembered before windows that have fully passed are cleared out.
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
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the run may proceed; otherwise <see langword="false"/> and
    /// <paramref name="retryAfter"/> is how long until the oldest run leaves the window.
    /// </returns>
    public bool TryClaim(BusinessId business, out TimeSpan retryAfter)
    {
        var now = _clock.UtcNow;
        var runs = _runsByBusiness.GetOrAdd(business, static _ => new Queue<DateTime>());

        // One business's queue is mutated under its own lock. ConcurrentDictionary makes finding the
        // queue safe; it does nothing for the read-modify-write inside one, and two simultaneous
        // presses racing on the same queue is the case this limit exists for.
        lock (runs)
        {
            while (runs.Count > 0 && now - runs.Peek() >= Window)
            {
                runs.Dequeue();
            }

            if (runs.Count >= MaxRunsPerWindow)
            {
                // The oldest run is the one whose departure frees a slot. Never negative: it is
                // still inside the window, or the loop above would have removed it.
                retryAfter = Window - (now - runs.Peek());
                return false;
            }

            runs.Enqueue(now);
        }

        PruneIdle(now);
        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>
    /// Forgets the businesses whose whole window has passed, once more than
    /// <see cref="MaxTrackedBusinesses"/> are remembered. Only an entry that would allow a run anyway
    /// is removed, so clearing out can never grant or deny one.
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
                if (runs.Count > 0 && now - runs.Peek() < Window)
                {
                    continue;
                }

                // Removed by key and value together, so a queue another caller replaced in the
                // meantime is left alone. A caller already holding this queue and waiting on its
                // lock would enqueue into a forgotten one and so get one extra allowance - which is
                // the allowance it was about to be granted anyway, since this business's whole
                // window had passed.
                _runsByBusiness.TryRemove(entry);
            }
        }
    }
}
