using Inventory.Application.SalesSync;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// The per-business synchronization lock of issue #1104 on its own: who gets it, who waits, who gives
/// up - and, the case that matters most, who may give it back.
///
/// <para><strong>Releasing a lock you do not hold is the failure this class exists to prevent.</strong>
/// A <c>finally</c> that released unconditionally would let a waiter that timed out hand back the
/// holder's lock while the holder was still reading Nayax and writing sales, and a third caller would
/// then run beside it - which is exactly the concurrent run the lock was added for. So the lease
/// carries an explicit acquired flag and the release is conditional on it, and these cases assert
/// that from both directions.</para>
/// </summary>
public class NayaxSalesSyncLockTests
{
    private static readonly BusinessId Business = BusinessId.From(1);
    private static readonly BusinessId OtherBusiness = BusinessId.From(2);

    [Fact]
    public async Task A_free_lock_is_acquired_immediately()
    {
        var syncLock = new NayaxSalesSyncLock();

        using var lease = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        Assert.True(lease.Acquired);
    }

    /// <summary>
    /// The hourly run's acquisition: a business whose sync is already running is skipped for this
    /// hour rather than queued behind it.
    /// </summary>
    [Fact]
    public async Task A_try_acquire_while_the_lock_is_held_does_not_acquire()
    {
        var syncLock = new NayaxSalesSyncLock();
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        using var second = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        Assert.False(second.Acquired);
    }

    /// <summary>
    /// The request-driven acquisition's shape, with a wait short enough to assert: the waiter runs
    /// out of patience and says so, rather than hanging or throwing.
    /// </summary>
    [Fact]
    public async Task A_waiter_that_runs_out_of_time_does_not_acquire()
    {
        var syncLock = new NayaxSalesSyncLock();
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        using var waiter = await syncLock.AcquireAsync(
            Business, TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.False(waiter.Acquired);
    }

    /// <summary>
    /// <strong>The case the whole design is for.</strong> A waiter that timed out is disposed - as
    /// every caller's <c>using</c> or <c>finally</c> does - and that must change nothing: the holder
    /// still holds the lock, so a third caller still cannot get in. Without the acquired flag the
    /// timed-out waiter's release would have let it.
    /// </summary>
    [Fact]
    public async Task A_timed_out_waiter_does_not_release_the_holders_lock()
    {
        var syncLock = new NayaxSalesSyncLock();
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        var waiter = await syncLock.AcquireAsync(
            Business, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.False(waiter.Acquired);

        // The waiter's own finally.
        waiter.Dispose();

        using var third = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        Assert.False(third.Acquired);

        // And once the holder is really done, the next caller gets in - so the lock was not simply
        // broken by the attempt.
        held.Dispose();
        using var afterHolder = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        Assert.True(afterHolder.Acquired);
    }

    /// <summary>
    /// Cancellation while waiting acquires nothing, so there is nothing to release: the holder keeps
    /// the lock and a third caller still cannot get in.
    /// </summary>
    [Fact]
    public async Task Cancellation_while_waiting_releases_nothing()
    {
        var syncLock = new NayaxSalesSyncLock();
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        var waiting = syncLock.AcquireAsync(Business, TimeSpan.FromSeconds(30), cancelled.Token);

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        using var third = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        Assert.False(third.Acquired);
    }

    /// <summary>
    /// A lease disposed twice - a <c>using</c> inside a method that also disposes it - must release
    /// once. A <c>SemaphoreSlim</c> released twice raises its count above its maximum, which would
    /// let two runs in at once: a far worse failure than a lock held slightly too long.
    /// </summary>
    [Fact]
    public async Task Disposing_a_lease_twice_releases_once()
    {
        var syncLock = new NayaxSalesSyncLock();

        var lease = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        using var first = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        using var second = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
    }

    /// <summary>
    /// The lock is per business and the table of gates is process-wide, so this is the isolation that
    /// matters: one business's run must never make another business wait.
    /// </summary>
    [Fact]
    public async Task One_businesss_lock_never_holds_another_business_up()
    {
        var syncLock = new NayaxSalesSyncLock();
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        using var other = await syncLock.TryAcquireAsync(OtherBusiness, CancellationToken.None);

        Assert.True(other.Acquired);
        Assert.Equal(2, syncLock.TrackedBusinesses);
    }

    /// <summary>
    /// Released, the lock is free again, and the business is still remembered: a gate is never
    /// forgotten, because forgetting one is the only way two callers could hold different gates for
    /// the same business.
    /// </summary>
    [Fact]
    public async Task A_released_lock_is_free_again_and_the_business_is_still_tracked()
    {
        var syncLock = new NayaxSalesSyncLock();

        using (var first = await syncLock.TryAcquireAsync(Business, CancellationToken.None))
        {
            Assert.True(first.Acquired);
        }

        using var second = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        Assert.True(second.Acquired);
        Assert.Equal(1, syncLock.TrackedBusinesses);
    }

    /// <summary>
    /// The wait a request uses is the issue's own value, stated once - and it is what the
    /// composition root's own lock waits, so no deployment gets a different one by default.
    /// </summary>
    [Fact]
    public void A_request_waits_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), NayaxSalesSyncLock.DefaultRequestWait);
        Assert.Equal(TimeSpan.FromSeconds(60), new NayaxSalesSyncLock().RequestWait);
    }

    /// <summary>
    /// The request-driven acquisition goes through the lock's own wait rather than a duration the
    /// caller chose, so "a request waits a minute" is one fact and not a value every call site
    /// repeats.
    /// </summary>
    [Fact]
    public async Task A_request_acquisition_waits_the_locks_own_request_wait()
    {
        var syncLock = new NayaxSalesSyncLock(TimeSpan.FromMilliseconds(20));
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);

        using var waiter = await syncLock.AcquireForRequestAsync(Business, CancellationToken.None);

        Assert.False(waiter.Acquired);
    }

    /// <summary>The lease nobody acquired is safe to dispose, which is what makes the branch-free
    /// <c>using</c> at the call site correct.</summary>
    [Fact]
    public void The_not_acquired_lease_is_safe_to_dispose()
    {
        Assert.False(NayaxSalesSyncLease.NotAcquired.Acquired);

        NayaxSalesSyncLease.NotAcquired.Dispose();
        NayaxSalesSyncLease.NotAcquired.Dispose();

        Assert.False(NayaxSalesSyncLease.NotAcquired.Acquired);
    }
}
