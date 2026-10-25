using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// The recent-sync skip of issue #843 (part of parent #839), at the use case that owns it.
///
/// The Dashboard calls <c>POST api/nayax-sales-sync</c> on every load, and one run is a Nayax
/// request per machine plus one, then persisting, costing and rebuilding inventory costs. Inside
/// <see cref="NayaxSalesSyncFreshness.RecentWindow"/> the previous run's result is already what the
/// operator is looking at, so the run is skipped: nothing is read from Nayax, nothing is persisted
/// or recosted, and - unlike the run limit of issue #681 - no allowance is spent.
///
/// What these tests are really about is the <em>order</em> of the three decisions and which of them
/// may record freshness. The refusals come first, so a business with no usable connection is never
/// told its sales are current; the skip comes next, before the limiter, so skipping is free; and
/// only a run that finished every step records the instant the next visit may skip on.
/// </summary>
public class SyncLatestNayaxSalesFreshnessTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_sync_within_the_window_reads_nothing_and_reports_when_the_sales_were_synced()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var nayax = SingleMachine();
        var store = EmptyStore();

        Assert.Null((await UseCase(nayax, store, clock, freshness).Handle()).RecentlySyncedAtUtc);

        clock.UtcNow = Now.AddMinutes(2).AddSeconds(59);
        var skipped = await UseCase(nayax, store, clock, freshness).Handle();

        Assert.Equal(Now, skipped.RecentlySyncedAtUtc);
        Assert.Null(skipped.RetryAfter);
        // The first run made these calls; the skipped one added none.
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A skip is not a rationed run: the allowance exists to bound runs against somebody else's API,
    /// and a skip makes no request at all. Spending one would mean a browser in a reload loop could
    /// still lock the business out of its own Refresh button.
    /// </summary>
    [Fact]
    public async Task A_skipped_sync_claims_no_rate_limit_allowance()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var limiter = new NayaxSalesSyncRateLimiter(clock);
        var nayax = SingleMachine();
        var useCase = UseCase(nayax, EmptyStore(), clock, freshness, limiter: limiter);

        await useCase.Handle();
        for (var skip = 0; skip < NayaxSalesSyncRateLimiter.MaxRunsPerWindow * 2; skip++)
        {
            Assert.Equal(Now, (await useCase.Handle()).RecentlySyncedAtUtc);
        }

        // Nine of the ten allowances are still unspent, so the operator's Refresh still runs.
        Assert.Null((await useCase.Handle(force: true)).RetryAfter);
    }

    [Fact]
    public async Task A_sync_once_the_window_has_passed_runs_in_full()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var nayax = SingleMachine();
        var store = EmptyStore();
        var useCase = UseCase(nayax, store, clock, freshness);

        await useCase.Handle();
        clock.UtcNow = Now + NayaxSalesSyncFreshness.RecentWindow;
        var second = await useCase.Handle();

        Assert.Null(second.RecentlySyncedAtUtc);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /// <summary>The first visit of a business since startup has no recorded run, so it syncs.</summary>
    [Fact]
    public async Task A_sync_with_no_recorded_run_for_the_key_runs_in_full()
    {
        var nayax = SingleMachine();

        Assert.Null((await UseCase(nayax, EmptyStore()).Handle()).RecentlySyncedAtUtc);

        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The Dashboard's Refresh control, which is why the skip is safe to have at all.</summary>
    [Fact]
    public async Task A_forced_sync_inside_the_window_runs_in_full()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var nayax = SingleMachine();
        var useCase = UseCase(nayax, EmptyStore(), clock, freshness);

        await useCase.Handle();
        var forced = await useCase.Handle(force: true);

        Assert.Null(forced.RecentlySyncedAtUtc);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// A forced run is a way past the freshness window and nothing else: the run limit still applies,
    /// so a Refresh button held down costs the upstream ten runs rather than thousands.
    /// </summary>
    [Fact]
    public async Task A_forced_sync_is_still_subject_to_the_run_limit()
    {
        var clock = new FakeClock(Now);
        var useCase = UseCase(SingleMachine(), EmptyStore(), clock, new NayaxSalesSyncFreshness(clock));

        for (var run = 0; run < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.Null((await useCase.Handle(force: true)).RetryAfter);
        }

        Assert.NotNull((await useCase.Handle(force: true)).RetryAfter);
    }

    /// <summary>
    /// A run that threw records nothing, so the next visit runs rather than reporting sales as
    /// current that were never read. The machine read fails here, which is the same boundary issue
    /// #730 uses for the coverage record.
    /// </summary>
    [Fact]
    public async Task A_failed_sync_does_not_make_the_next_one_skip()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var failing = SingleMachine();
        failing.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Nayax unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(failing, EmptyStore(), clock, freshness).Handle());

        var nayax = SingleMachine();
        Assert.Null((await UseCase(nayax, EmptyStore(), clock, freshness).Handle()).RecentlySyncedAtUtc);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_cancelled_sync_does_not_make_the_next_one_skip()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var cancelling = SingleMachine();
        cancelling.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => UseCase(cancelling, EmptyStore(), clock, freshness).Handle());

        Assert.Null((await UseCase(SingleMachine(), EmptyStore(), clock, freshness).Handle()).RecentlySyncedAtUtc);
    }

    /// <summary>
    /// A refused run never happened, so it is not evidence that the sales are current either. This is
    /// the case that would otherwise be self-sustaining: a rate-limited run recording freshness would
    /// let the skip take over from the limit and stretch one stale snapshot indefinitely.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_sync_does_not_make_the_next_one_skip()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var limiter = new NayaxSalesSyncRateLimiter(clock);
        for (var spend = 0; spend < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; spend++)
        {
            Assert.True(limiter.TryClaim(Inventory.Domain.Tenancy.BusinessId.From(TestSalesSync.BusinessId), out _));
        }

        var refused = await UseCase(SingleMachine(), EmptyStore(), clock, freshness, limiter: limiter).Handle();

        Assert.NotNull(refused.RetryAfter);
        Assert.Null(refused.RecentlySyncedAtUtc);
        Assert.Null(freshness.FindRecentSync(
            Inventory.Domain.Tenancy.BusinessId.From(TestSalesSync.BusinessId), credentialRevision: 1));
    }

    /// <summary>
    /// Saving or replacing the business's Nayax credentials bumps the revision, which is half the
    /// freshness key: the next visit runs in full even seconds later, because whether sales now
    /// arrive under the new credentials is exactly what the operator is checking.
    /// </summary>
    [Fact]
    public async Task Replacing_the_credentials_makes_the_next_sync_run_in_full()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var credentials = new FakeNayaxOperationCredentials(credentialRevision: 4);
        var nayax = SingleMachine();
        var useCase = UseCase(nayax, EmptyStore(), clock, freshness, credentials: credentials);

        await useCase.Handle();
        Assert.Equal(Now, (await useCase.Handle()).RecentlySyncedAtUtc);

        credentials.CredentialRevision = 5;
        var afterSave = await useCase.Handle();

        Assert.Null(afterSave.RecentlySyncedAtUtc);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// The freshness memory is a process-wide singleton, so this is the isolation that matters: one
    /// business's successful run must not let another business's Dashboard show nothing new.
    /// </summary>
    [Fact]
    public async Task One_businesss_sync_never_lets_another_business_skip()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        var first = SingleMachine();
        var second = SingleMachine();

        await UseCase(first, EmptyStore(), clock, freshness).Handle();
        var otherBusiness = new SyncLatestNayaxSales(
            second.Object,
            EmptyStore().Object,
            new NayaxSalesSyncRateLimiter(clock),
            freshness,
            new FakeNayaxOperationCredentials(),
            new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(TestSalesSync.BusinessId + 1),
            new FakeProviderDataCoverageStore(),
            clock);

        Assert.Null((await otherBusiness.Handle()).RecentlySyncedAtUtc);
        second.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The connection, the status gate and the support-session refusal are resolved before the skip
    /// is considered, so a business that cannot read Nayax at all gets today's refusal rather than a
    /// reassuring "synchronized moments ago". The recorded freshness is deliberately a real one here:
    /// a successful run has happened, and the refusal still wins.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_refused_connection_is_reported_as_today_even_inside_the_window(
        NayaxConnectionStatus status)
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        await UseCase(SingleMachine(), EmptyStore(), clock, freshness).Handle();

        var nayax = SingleMachine();
        var refusing = FakeNayaxOperationCredentials.Refusing(status);

        var refusal = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => UseCase(nayax, EmptyStore(), clock, freshness, credentials: refusing).Handle());

        Assert.Equal(status, refusal.Status);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_support_session_is_refused_even_inside_the_window()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        await UseCase(SingleMachine(), EmptyStore(), clock, freshness).Handle();

        var nayax = SingleMachine();

        await Assert.ThrowsAsync<SupportSessionNayaxUnavailableException>(
            () => UseCase(
                nayax,
                EmptyStore(),
                clock,
                freshness,
                credentials: FakeNayaxOperationCredentials.InSupportSession()).Handle());

        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A run that does happen is unchanged: it reads the machine list fresh and records the coverage
    /// it accounted for, which is what keeps "no sales" distinguishable from "no sales data"
    /// (issue #730). The skip must not have moved either of those.
    /// </summary>
    [Fact]
    public async Task A_run_that_happens_still_reads_the_machine_list_and_records_coverage()
    {
        var clock = new FakeClock(Now);
        var coverage = new FakeProviderDataCoverageStore();
        var nayax = SingleMachine(saleAtUtc: Now.AddMinutes(-30));

        await UseCase(nayax, EmptyStore(), clock, coverage: coverage).Handle();

        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        var recorded = Assert.Single(coverage.Recorded);
        Assert.Equal(ProviderDataSource.SalesSync, recorded.Source);
        Assert.Equal(Now.AddMinutes(-30), recorded.FromUtc);
        Assert.Equal(Now, recorded.ToUtc);
    }

    private static SyncLatestNayaxSales UseCase(
        Mock<INayaxLynxClient> nayax,
        Mock<ILatestNayaxSalesStore> store,
        FakeClock? clock = null,
        NayaxSalesSyncFreshness? freshness = null,
        FakeProviderDataCoverageStore? coverage = null,
        FakeNayaxOperationCredentials? credentials = null,
        NayaxSalesSyncRateLimiter? limiter = null)
    {
        var sharedClock = clock ?? new FakeClock(Now);
        return new SyncLatestNayaxSales(
            nayax.Object,
            store.Object,
            limiter ?? new NayaxSalesSyncRateLimiter(sharedClock),
            freshness ?? new NayaxSalesSyncFreshness(sharedClock),
            credentials ?? new FakeNayaxOperationCredentials(),
            new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(TestSalesSync.BusinessId),
            coverage ?? new FakeProviderDataCoverageStore(),
            sharedClock);
    }

    private static Mock<INayaxLynxClient> SingleMachine(DateTime? saleAtUtc = null)
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
                    AuthorizationDateTimeGmt = saleAtUtc ?? Now.AddMinutes(-5),
                },
            });
        return nayax;
    }

    private static Mock<ILatestNayaxSalesStore> EmptyStore()
    {
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        return store;
    }
}
