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

    /// <summary>
    /// Issue #606. Clearing out businesses above the tracking threshold must expire the attempts
    /// that have left the window and nothing else. A business whose <em>oldest</em> attempt has
    /// expired while four later ones are still live has one slot free, not five: forgetting it
    /// wholesale would hand it a fresh allowance and let the upstream take five more runs inside the
    /// same ten minutes.
    /// </summary>
    [Fact]
    public void Cleanup_expires_the_attempts_that_left_the_window_without_discarding_the_live_ones()
    {
        var clock = new FakeClock(Start);
        var limiter = new NayaxConnectionTestRateLimiter(clock);

        // One run at minute zero, then the remaining four at minute nine.
        Assert.True(limiter.TryClaim(First, out _));
        clock.UtcNow = Start.AddMinutes(9);
        for (var run = 2; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        // At minute ten only the first run has left the window - and enough other businesses arrive
        // to push the tracking threshold over, so cleanup runs over this business's history.
        clock.UtcNow = Start.AddMinutes(10);
        ClaimOnceForOtherBusinesses(limiter, NayaxConnectionTestRateLimiter.MaxTrackedBusinesses + 1);

        // Exactly one slot reopened, and the refusal still points at the oldest live attempt.
        Assert.True(limiter.TryClaim(First, out var allowedRetryAfter));
        Assert.Equal(TimeSpan.Zero, allowedRetryAfter);
        Assert.False(limiter.TryClaim(First, out var refusedRetryAfter));
        Assert.Equal(TimeSpan.FromMinutes(9), refusedRetryAfter);

        // The next slot reopens when a minute-nine attempt leaves the window, not before.
        clock.UtcNow = Start.AddMinutes(18).AddSeconds(59);
        Assert.False(limiter.TryClaim(First, out _));
        clock.UtcNow = Start.AddMinutes(19);
        Assert.True(limiter.TryClaim(First, out _));
    }

    /// <summary>
    /// Cleanup is idempotent over a mixed history: however many times it runs, it removes the two
    /// attempts that have left the window and leaves the three that have not.
    /// </summary>
    [Fact]
    public void Repeated_cleanup_leaves_a_mixed_history_at_its_live_attempts()
    {
        var clock = new FakeClock(Start);
        var limiter = new NayaxConnectionTestRateLimiter(clock);

        Assert.True(limiter.TryClaim(First, out _));
        Assert.True(limiter.TryClaim(First, out _));
        clock.UtcNow = Start.AddMinutes(6);
        for (var run = 3; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        clock.UtcNow = Start.AddMinutes(10);

        // Every one of these claims is above the threshold, so cleanup walks this business's history
        // over and over.
        ClaimOnceForOtherBusinesses(limiter, NayaxConnectionTestRateLimiter.MaxTrackedBusinesses * 2);

        Assert.True(limiter.TryClaim(First, out _));
        Assert.True(limiter.TryClaim(First, out _));
        Assert.False(limiter.TryClaim(First, out var refusedRetryAfter));
        Assert.Equal(TimeSpan.FromMinutes(6), refusedRetryAfter);
    }

    /// <summary>
    /// The threshold still does its job: a business that really has gone quiet for a whole window is
    /// forgotten, so the dictionary cannot grow without bound.
    /// </summary>
    [Fact]
    public void Cleanup_forgets_the_businesses_whose_whole_window_has_passed()
    {
        var clock = new FakeClock(Start);
        var limiter = new NayaxConnectionTestRateLimiter(clock);

        var idleBusinesses = NayaxConnectionTestRateLimiter.MaxTrackedBusinesses + 1;
        ClaimOnceForOtherBusinesses(limiter, idleBusinesses);
        Assert.Equal(idleBusinesses, limiter.TrackedBusinesses);

        clock.UtcNow = Start + NayaxConnectionTestRateLimiter.Window;
        Assert.True(limiter.TryClaim(First, out _));

        // Only the business that just claimed is still remembered.
        Assert.Equal(1, limiter.TrackedBusinesses);
    }

    /// <summary>
    /// The threshold is a hint about memory, never a cap on anybody's quota. Crossing it must not
    /// reset a busy business's window, and must not refuse a business that has not used its
    /// allowance.
    /// </summary>
    [Fact]
    public void Exceeding_the_tracking_threshold_neither_resets_nor_refuses_an_unrelated_business()
    {
        var limiter = new NayaxConnectionTestRateLimiter(new FakeClock(Start));

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(First, out _));
        }

        ClaimOnceForOtherBusinesses(limiter, NayaxConnectionTestRateLimiter.MaxTrackedBusinesses * 2);
        Assert.True(limiter.TrackedBusinesses > NayaxConnectionTestRateLimiter.MaxTrackedBusinesses);

        // The exhausted business is still exhausted, and an untouched one still gets all five runs.
        Assert.False(limiter.TryClaim(First, out var refusedRetryAfter));
        Assert.Equal(NayaxConnectionTestRateLimiter.Window, refusedRetryAfter);

        for (var run = 1; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.True(limiter.TryClaim(Second, out _));
        }

        Assert.False(limiter.TryClaim(Second, out _));
    }

    /// <summary>
    /// Issue #606, the concurrent half: claims and cleanup race on the same business while the
    /// tracking threshold is exceeded, which is when cleanup is entitled to forget that business.
    /// Whatever the interleaving, the window may grant <see cref="NayaxConnectionTestRateLimiter.MaxRunsPerWindow"/>
    /// runs and not one more - a claim counted into a queue cleanup has already detached is a claim
    /// nothing accounted for.
    /// </summary>
    [Fact]
    public void Claims_racing_cleanup_grant_the_allowance_exactly_once()
    {
        const int claimers = 8;
        const int roundsPerClaimer = 8;

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var clock = new FakeClock(Start);
            var limiter = new NayaxConnectionTestRateLimiter(clock);

            // The target's one attempt has left the window by the time the race starts, so cleanup
            // may forget it, while enough live businesses keep the threshold exceeded so that every
            // granted claim triggers another cleanup pass.
            Assert.True(limiter.TryClaim(First, out _));
            clock.UtcNow = Start + NayaxConnectionTestRateLimiter.Window;
            ClaimOnceForOtherBusinesses(limiter, NayaxConnectionTestRateLimiter.MaxTrackedBusinesses + 1);

            var granted = 0;
            using var start = new Barrier(claimers);
            var threads = new Thread[claimers];
            for (var claimer = 0; claimer < claimers; claimer++)
            {
                var noise = 100_000 + (claimer * roundsPerClaimer);
                threads[claimer] = new Thread(() =>
                {
                    start.SignalAndWait();
                    for (var round = 0; round < roundsPerClaimer; round++)
                    {
                        // A never-seen business always gets its run, and so always triggers cleanup.
                        limiter.TryClaim(BusinessId.From(noise + round), out _);

                        if (limiter.TryClaim(First, out _))
                        {
                            Interlocked.Increment(ref granted);
                        }
                    }
                });
            }

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            Assert.Equal(NayaxConnectionTestRateLimiter.MaxRunsPerWindow, granted);
        }
    }

    /// <summary>
    /// Claims one run each for <paramref name="count"/> businesses that no test asserts on, which is
    /// how the tracking threshold is crossed and cleanup made to run.
    /// </summary>
    private static void ClaimOnceForOtherBusinesses(NayaxConnectionTestRateLimiter limiter, int count)
    {
        for (var offset = 0; offset < count; offset++)
        {
            Assert.True(limiter.TryClaim(BusinessId.From(1000 + offset), out _));
        }
    }
}
