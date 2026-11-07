using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// The Busy outcome of issue #1104 at the use case that owns it: what a request does when a
/// synchronization for its business is already running.
///
/// <para><strong>What these cases are really about is how much a waiter is allowed to cost.</strong>
/// A request that cannot get the lock must leave the world exactly as it found it: no Nayax call, no
/// write, no coverage record and - the one that would be easy to get wrong - no rate-limit claim.
/// Claiming an allowance for a run that never happened would let a background run that is holding
/// the lock spend the operator's own Refresh budget, so that a person pressing Refresh after the
/// hourly run finished would be told they had refreshed too often.</para>
///
/// <para>The order is asserted too: the freshness skip of issue #843 still happens <em>before</em>
/// the lock is awaited, because a run with nothing to read must not wait a minute to say so.</para>
/// </summary>
public class SyncLatestNayaxSalesLockTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

    private static readonly BusinessId Business = BusinessId.From(TestSalesSync.BusinessId);

    /// <summary>
    /// The lock these cases use, with a wait short enough that "the request gave up" is assertable in
    /// milliseconds rather than in the real minute production waits. What is being tested is what a
    /// waiter that gave up costs, which the duration does not change;
    /// <c>NayaxSalesSyncLockTests</c> pins the production value itself.
    /// </summary>
    private static NayaxSalesSyncLock ShortWaitLock() =>
        new(TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// The lock is taken by something else - here, a lease held exactly as the hourly reconciliation
    /// holds one - and the request's wait is short enough to assert.
    /// </summary>
    [Fact]
    public async Task A_sync_that_cannot_get_the_lock_answers_busy_and_touches_nothing()
    {
        var syncLock = ShortWaitLock();
        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        Assert.True(held.Acquired);

        var clock = new FakeClock(Now);
        var nayax = SingleMachine();
        var store = TestSalesSync.EmptyStoreMock();
        var coverage = new FakeProviderDataCoverageStore();
        var limiter = new NayaxSalesSyncRateLimiter(clock);

        var outcome = await UseCase(nayax, store, clock, coverage, limiter, syncLock).Handle(force: true);

        Assert.True(outcome.IsBusy);
        Assert.Null(outcome.RetryAfter);
        Assert.Null(outcome.RecentlySyncedAtUtc);

        // No Nayax call at all: neither the machine list nor any machine's last sales.
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        nayax.Verify(
            x => x.GetMachineLastSalesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);

        // No write and no drain.
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()), Times.Never);

        // No coverage: a run that read nothing accounted for nothing (issue #730).
        Assert.Empty(coverage.Recorded);
    }

    /// <summary>
    /// The allowance, which is the part that would be easy to get wrong. A busy attempt claims none,
    /// so the business's ten runs per two minutes are all still there once the lock is free.
    /// </summary>
    [Fact]
    public async Task A_busy_sync_claims_no_rate_limit_allowance()
    {
        var syncLock = ShortWaitLock();
        var clock = new FakeClock(Now);
        var limiter = new NayaxSalesSyncRateLimiter(clock);

        using (var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None))
        {
            Assert.True(held.Acquired);

            for (var attempt = 0; attempt < NayaxSalesSyncRateLimiter.MaxRunsPerWindow * 2; attempt++)
            {
                var busy = await UseCase(
                        SingleMachine(),
                        TestSalesSync.EmptyStoreMock(),
                        clock,
                        new FakeProviderDataCoverageStore(),
                        limiter,
                        syncLock)
                    .Handle(force: true);

                Assert.True(busy.IsBusy);
            }
        }

        // Every allowance is still unspent, so the operator's own Refresh still runs - twenty times
        // over, had they wanted to.
        for (var run = 0; run < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; run++)
        {
            var outcome = await UseCase(
                    SingleMachine(),
                    TestSalesSync.EmptyStoreMock(),
                    clock,
                    new FakeProviderDataCoverageStore(),
                    limiter,
                    syncLock)
                .Handle(force: true);

            Assert.Null(outcome.RetryAfter);
            Assert.False(outcome.IsBusy);
        }
    }

    /// <summary>
    /// Once the lock is free the very next request runs in full, so a busy answer is a moment rather
    /// than a state.
    /// </summary>
    [Fact]
    public async Task A_sync_runs_in_full_once_the_lock_is_free()
    {
        var syncLock = ShortWaitLock();
        var clock = new FakeClock(Now);

        using (var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None))
        {
            Assert.True(
                (await UseCase(SingleMachine(), TestSalesSync.EmptyStoreMock(), clock, syncLock: syncLock)
                    .Handle(force: true)).IsBusy);
        }

        var nayax = SingleMachine();
        var outcome = await UseCase(nayax, TestSalesSync.EmptyStoreMock(), clock, syncLock: syncLock)
            .Handle(force: true);

        Assert.False(outcome.IsBusy);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The lock is per business, so one business's run never makes another business's Dashboard wait
    /// a minute and then report a conflict.
    /// </summary>
    [Fact]
    public async Task One_businesss_run_never_makes_another_business_busy()
    {
        var syncLock = ShortWaitLock();
        var clock = new FakeClock(Now);

        using var held = await syncLock.TryAcquireAsync(
            BusinessId.From(TestSalesSync.BusinessId + 1), CancellationToken.None);

        var outcome = await UseCase(SingleMachine(), TestSalesSync.EmptyStoreMock(), clock, syncLock: syncLock)
            .Handle(force: true);

        Assert.False(outcome.IsBusy);
    }

    /// <summary>
    /// The order the issue asks for: the freshness skip is decided before the lock is awaited, so a
    /// business that synchronized moments ago answers immediately instead of waiting out the lock for
    /// a run it was going to skip anyway.
    /// </summary>
    [Fact]
    public async Task The_freshness_skip_is_answered_without_waiting_for_the_lock()
    {
        var syncLock = ShortWaitLock();
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var useCase = UseCase(
            SingleMachine(), TestSalesSync.EmptyStoreMock(), clock, syncLock: syncLock, freshness: freshness);

        // One real run, which records freshness.
        Assert.False((await useCase.Handle()).IsBusy);

        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        Assert.True(held.Acquired);

        var skipped = await useCase.Handle();

        Assert.Equal(Now, skipped.RecentlySyncedAtUtc);
        Assert.False(skipped.IsBusy);
    }

    /// <summary>
    /// A run holding the lock through <see cref="NayaxSalesSyncLockMode.AlreadyHeld"/> - the hourly
    /// reconciliation's call - does not try to take it again, which is what lets the reconciliation
    /// keep the lease across its own evidence writes.
    /// </summary>
    [Fact]
    public async Task A_run_whose_caller_holds_the_lease_does_not_take_it_again()
    {
        var syncLock = ShortWaitLock();
        var clock = new FakeClock(Now);

        using var held = await syncLock.TryAcquireAsync(Business, CancellationToken.None);
        Assert.True(held.Acquired);

        var nayax = SingleMachine();
        var outcome = await UseCase(nayax, TestSalesSync.EmptyStoreMock(), clock, syncLock: syncLock)
            .Handle(force: true, NayaxSalesSyncLockMode.AlreadyHeld, observer: null, CancellationToken.None);

        Assert.False(outcome.IsBusy);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// What an observer is told, which is what the hourly reconciliation compares over: the run's
    /// start instant, each machine's items exactly as read, and how many the save created.
    /// </summary>
    [Fact]
    public async Task An_observer_is_told_the_run_start_each_machines_items_and_the_added_count()
    {
        var clock = new FakeClock(Now);
        var observer = new RecordingObserver();

        await UseCase(SingleMachine(), TestSalesSync.StoreMock(), clock)
            .Handle(force: true, NayaxSalesSyncLockMode.Acquire, observer, CancellationToken.None);

        Assert.Equal(Now, observer.ReadStartedAtUtc);
        var machine = Assert.Single(observer.Machines);
        Assert.Equal(1, machine.MachineId);
        Assert.Equal(100, Assert.Single(machine.Sales).TransactionID);
        Assert.Equal(1, observer.InsertedCount);
    }

    private static SyncLatestNayaxSales UseCase(
        Mock<INayaxLynxClient> nayax,
        Mock<ILatestNayaxSalesStore> store,
        FakeClock clock,
        FakeProviderDataCoverageStore? coverage = null,
        NayaxSalesSyncRateLimiter? limiter = null,
        NayaxSalesSyncLock? syncLock = null,
        NayaxSalesSyncFreshness? freshness = null) =>
        new(
            nayax.Object,
            store.Object,
            limiter ?? new NayaxSalesSyncRateLimiter(clock),
            freshness ?? new NayaxSalesSyncFreshness(clock),
            syncLock ?? ShortWaitLock(),
            new FakeNayaxOperationCredentials(),
            new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(TestSalesSync.BusinessId),
            coverage ?? new FakeProviderDataCoverageStore(),
            // Not live via SQS: these cases are about the lock, and a live business never reaches it
            // (issue #1106).
            new InventoryApi.Tests.Application.NayaxSqs.FakeNayaxSqsLiveStatusStore(),
            new Inventory.Application.NayaxSqs.NayaxSqsReaderHealth(),
            clock);

    private static Mock<INayaxLynxClient> SingleMachine()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new()
                {
                    TransactionID = 100,
                    MachineID = 1,
                    SettlementValue = 3m,
                    AuthorizationDateTimeGmt = Now.AddMinutes(-5),
                },
            });
        return nayax;
    }

    private sealed class RecordingObserver : INayaxLastSalesObserver
    {
        public DateTime ReadStartedAtUtc { get; private set; }

        public List<(long MachineId, IReadOnlyList<NayaxLastSalesReport> Sales)> Machines { get; } = [];

        public int InsertedCount { get; private set; }

        public void RunStarted(DateTime readStartedAtUtc) => ReadStartedAtUtc = readStartedAtUtc;

        public void MachineRead(long machineId, IReadOnlyList<NayaxLastSalesReport> sales) =>
            Machines.Add((machineId, sales));

        public void SalesPersisted(int insertedCount) => InsertedCount = insertedCount;
    }
}
