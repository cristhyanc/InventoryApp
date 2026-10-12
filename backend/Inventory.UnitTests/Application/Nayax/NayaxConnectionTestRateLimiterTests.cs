using Inventory.Application.Nayax;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// The per-business connection-test rate limit of issue #536: five runs in ten minutes, sliding, and
/// each business counted on its own.
///
/// One test run is eight Nayax calls against somebody else's API, so the boundary cases matter: the
/// fifth run is allowed and the sixth is not, the window slides rather than resetting on the clock,
/// and one busy business never spends another's allowance.
/// </summary>
public class NayaxConnectionTestRateLimiterTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly BusinessId First = BusinessId.From(1);
    private static readonly BusinessId Second = BusinessId.From(2);

    [Fact]
    public void The_allowed_runs_are_claimed_and_the_next_one_is_refused()
    {
        var limiter = new NayaxConnectionTestRateLimiter(new FakeClock(Start));

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out var retryAfter), $"Run {run} was refused.");
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }

        Assert.False(limiter.TryClaim(First, out var refusedRetryAfter));
        Assert.Equal(NayaxConnectionTestRateLimiter.Window, refusedRetryAfter);
    }

    /// <summary>
    /// The window slides with the oldest run, not with a clock boundary: a run becomes available
    /// exactly when the first one leaves the window, and the four still inside it keep their places.
    /// </summary>
    [Fact]
    public void A_slot_reopens_when_the_oldest_run_leaves_the_window()
    {
        var clock = new FakeClock(Start);
        var limiter = new NayaxConnectionTestRateLimiter(clock);

        Assert.True(limiter.TryClaim(First, out _));
        clock.UtcNow = Start.AddMinutes(1);
        for (var run = 2; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        // Nine minutes after the first run: it is still inside the ten-minute window, so the
        // business is still at its limit and is told to wait the remaining minute.
        clock.UtcNow = Start.AddMinutes(9);
        Assert.False(limiter.TryClaim(First, out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);

        clock.UtcNow = Start.AddMinutes(10);
        Assert.True(limiter.TryClaim(First, out _));

        // And only one slot reopened: the runs from minute one are still inside the window.
        Assert.False(limiter.TryClaim(First, out _));
    }

    [Fact]
    public void A_business_that_has_waited_out_the_whole_window_starts_again()
    {
        var clock = new FakeClock(Start);
        var limiter = new NayaxConnectionTestRateLimiter(clock);

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        clock.UtcNow = Start + NayaxConnectionTestRateLimiter.Window;

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        Assert.False(limiter.TryClaim(First, out _));
    }

    /// <summary>
    /// The bucket is the business. One business exhausting its allowance must not stop another from
    /// connecting Nayax at all.
    /// </summary>
    [Fact]
    public void One_business_hitting_the_limit_does_not_spend_another_s_allowance()
    {
        var limiter = new NayaxConnectionTestRateLimiter(new FakeClock(Start));

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        Assert.False(limiter.TryClaim(First, out _));
        Assert.True(limiter.TryClaim(Second, out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    /// <summary>
    /// A clock that moved backwards must not reopen a slot: the fail-safe direction for a rate limit
    /// is to refuse, and a negative elapsed time is never "the window has passed".
    /// </summary>
    [Fact]
    public void A_clock_that_moved_backwards_does_not_reopen_a_slot()
    {
        var clock = new FakeClock(Start);
        var limiter = new NayaxConnectionTestRateLimiter(clock);

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        clock.UtcNow = Start.AddMinutes(-30);

        Assert.False(limiter.TryClaim(First, out _));
    }

    [Fact]
    public void A_business_is_remembered_only_once_however_many_runs_it_claims()
    {
        var limiter = new NayaxConnectionTestRateLimiter(new FakeClock(Start));

        limiter.TryClaim(First, out _);
        limiter.TryClaim(First, out _);
        limiter.TryClaim(Second, out _);

        Assert.Equal(2, limiter.TrackedBusinesses);
    }

    /// <summary>
    /// The agreed numbers, stated once. Changing either is a deliberate change to how often an
    /// Owner may test, and to how much traffic this application can direct at Nayax.
    /// </summary>
    [Fact]
    public void The_limit_is_five_runs_per_ten_minutes()
    {
        Assert.Equal(5, NayaxConnectionTestRateLimiter.MaxRunsPerWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), NayaxConnectionTestRateLimiter.Window);
    }
}
