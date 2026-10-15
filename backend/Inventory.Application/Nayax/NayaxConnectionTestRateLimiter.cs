using Inventory.Application.Tenancy;
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

    private readonly BusinessSlidingWindowLimiter _limiter;

    public NayaxConnectionTestRateLimiter(IClock clock)
    {
        _limiter = new BusinessSlidingWindowLimiter(clock, MaxRunsPerWindow, Window, MaxTrackedBusinesses);
    }

    /// <summary>How many businesses are currently remembered.</summary>
    public int TrackedBusinesses => _limiter.TrackedBusinesses;

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
    public bool TryClaim(BusinessId business, out TimeSpan retryAfter) =>
        _limiter.TryClaim(business, out retryAfter);
}
