using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SalesSync;

/// <summary>
/// "At most <see cref="MaxRunsPerWindow"/> latest-sales synchronizations per business per
/// <see cref="Window"/>" (issue #681), claimed before any Nayax call is made.
///
/// <para><strong>Why this endpoint needs one at all.</strong> <c>POST api/nayax-sales-sync</c> is
/// the most expensive call in the application and the easiest to repeat: one run reads the machine
/// list and then <em>every</em> machine's last sales, so a business with twenty machines spends
/// twenty-one Nayax requests per press, and it is called by the home Dashboard on load - which a
/// browser repeats on every visit, every back-navigation and every refresh. It also writes:
/// deduplicating, classifying, costing and persisting the batch, then rebuilding inventory costs for
/// the products a completed sale touched. Until this limit existed, nothing bounded either the
/// requests against somebody else's API or the write amplification in this one.</para>
///
/// <para>The limit is per business, not per actor, for the reason
/// <c>NayaxConnectionTestRateLimiter</c>'s is: what it protects is the business's own Nayax operator
/// account and its own database rows, so a second member must not start the window again from zero
/// against the same upstream. It is a sliding window rather than a fixed one, because a fixed bucket
/// allows <see cref="MaxRunsPerWindow"/> runs at the end of one bucket and the same number at the
/// start of the next - twice the intended rate at the moment it matters most.</para>
///
/// <para>It is deliberately a <em>separate</em> bucket from the connection test's, with its own
/// limit: the two protect different work at very different rates, and one shared allowance would
/// mean a dashboard refresh could leave an Owner unable to test their connection. The two share the
/// mechanism itself, <c>BusinessSlidingWindowLimiter</c>, and only their limits differ.</para>
///
/// <para>The window is kept in memory rather than in a column, like the connection test's and
/// <c>MemberProfileRefreshThrottle</c>: how often a dashboard was opened is not part of the
/// business's record, and a restart costing a business one extra allowance is a far better trade
/// than a table and a write per attempt. On a single instance
/// (docs/architecture.md § SQLite operating assumptions) in-process state is the whole truth; a
/// second instance would mean this allowance per instance, which is still bounded.</para>
/// </summary>
public sealed class NayaxSalesSyncRateLimiter
{
    /// <summary>
    /// How many synchronizations one business may start inside <see cref="Window"/>.
    ///
    /// Chosen to be invisible to people and decisive against a loop: the Dashboard triggers one run
    /// per load, so ten in two minutes covers several members opening and re-opening the page
    /// together, while a tab stuck in a retry loop costs the upstream ten runs instead of thousands.
    /// A refused run leaves the Dashboard showing its persisted figures flagged as not synchronized,
    /// which is the same state it already shows when Nayax cannot be reached.
    /// </summary>
    public const int MaxRunsPerWindow = 10;

    /// <summary>The sliding window the run count is measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many businesses may be remembered before the businesses whose windows have fully passed
    /// are cleared out. It is a hint about memory, never a cap: crossing it expires runs that have
    /// left the window and forgets the businesses left with none, and it neither grants, denies nor
    /// resets an allowance (the rule issue #606 established for the connection test's limiter).
    /// </summary>
    public const int MaxTrackedBusinesses = 256;

    private readonly BusinessSlidingWindowLimiter _limiter;

    public NayaxSalesSyncRateLimiter(IClock clock)
    {
        _limiter = new BusinessSlidingWindowLimiter(clock, MaxRunsPerWindow, Window, MaxTrackedBusinesses);
    }

    /// <summary>How many businesses are currently remembered.</summary>
    public int TrackedBusinesses => _limiter.TrackedBusinesses;

    /// <summary>
    /// Claims one run for <paramref name="business"/> and answers whether this caller got it.
    ///
    /// The claim is taken <em>before</em> the Nayax calls, not after them, which is what makes
    /// several simultaneous loads cost one allowance each rather than all passing a count nothing
    /// has incremented yet. A run that then fails is not refunded: a failing sync is exactly the run
    /// that gets repeated, and it costs the upstream the same requests as a successful one.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the run may proceed; otherwise <see langword="false"/> and
    /// <paramref name="retryAfter"/> is how long until the oldest run leaves the window.
    /// </returns>
    public bool TryClaim(BusinessId business, out TimeSpan retryAfter) =>
        _limiter.TryClaim(business, out retryAfter);
}
