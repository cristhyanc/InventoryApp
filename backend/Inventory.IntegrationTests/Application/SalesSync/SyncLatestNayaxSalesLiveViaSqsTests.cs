using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Application.SalesSync;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;
using Inventory.Domain.NayaxSqs;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.NayaxSqs;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// The on-load synchronization skip for a business whose sales arrive live from SQS (issue #1106,
/// child of #1100), at the use case that owns it.
///
/// <para>The Dashboard calls <c>POST api/nayax-sales-sync</c> on every load, and one run is a Nayax
/// request per machine plus one. Once the queue reader of issue #1103 is delivering a business's
/// sales there is nothing for that run to read - but only while the queue path is demonstrably
/// working, which is what every case below is about: an enabled sender, a reader that polled moments
/// ago without failing, and this business's own reconciliation having fully succeeded recently. Each
/// of the three failing on its own sends the business back to exactly today's behaviour, with the
/// reason reported beside the answer.</para>
/// </summary>
public class SyncLatestNayaxSalesLiveViaSqsTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// <strong>The headline case.</strong> A business on SQS with a healthy reader is live: the
    /// unforced call reads nothing from Nayax, persists nothing, rebuilds nothing, and reports when
    /// its queue last delivered.
    /// </summary>
    [Fact]
    public async Task A_business_on_a_healthy_queue_is_live_and_reads_nothing()
    {
        var nayax = SingleMachine();
        var store = TestSalesSync.EmptyStoreMock();
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(
            reconciliationSucceededAtUtc: Now.AddMinutes(-30),
            lastMessageReceivedAtUtc: Now.AddMinutes(-4));

        var outcome = await UseCase(nayax, store, sqsStatus: sqs, readerHealth: Polling()).Handle();

        Assert.True(outcome.LiveViaSqs);
        Assert.Null(outcome.SqsNotLiveReason);
        Assert.Equal(Now.AddMinutes(-4), outcome.LastSqsMessageReceivedAtUtc);
        // Not the recent-sync skip, not busy and not rationed: a different answer from all three.
        Assert.Null(outcome.RecentlySyncedAtUtc);
        Assert.False(outcome.IsBusy);
        Assert.Null(outcome.RetryAfter);

        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        nayax.Verify(x => x.GetMachineLastSalesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// <strong>A quiet queue is not a failure.</strong> No message for hours - or ever - keeps the
    /// business live, because the time since the last message is deliberately not a condition: an
    /// overnight fleet that sold nothing would otherwise send every business back to one Nayax call
    /// per machine at breakfast.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(600)]
    public async Task A_quiet_queue_with_a_healthy_reader_is_live(int? minutesSinceLastMessage)
    {
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(
            reconciliationSucceededAtUtc: Now.AddMinutes(-30),
            lastMessageReceivedAtUtc: minutesSinceLastMessage is { } minutes
                ? Now.AddMinutes(-minutes)
                : null);
        var nayax = SingleMachine();

        var outcome = await UseCase(nayax, sqsStatus: sqs, readerHealth: Polling()).Handle();

        Assert.True(outcome.LiveViaSqs);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// <strong>A live skip claims no rate-limit allowance.</strong> The allowance bounds runs against
    /// somebody else's API and a live skip makes no request at all, so a browser in a reload loop
    /// must not be able to spend the business's own Refresh budget on nothing.
    /// </summary>
    [Fact]
    public async Task A_live_business_claims_no_rate_limit_allowance()
    {
        var clock = new FakeClock(Now);
        var limiter = new NayaxSalesSyncRateLimiter(clock);
        var useCase = UseCase(
            SingleMachine(),
            clock: clock,
            limiter: limiter,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: Polling());

        for (var load = 0; load < NayaxSalesSyncRateLimiter.MaxRunsPerWindow * 2; load++)
        {
            Assert.True((await useCase.Handle()).LiveViaSqs);
        }

        // Every allowance is still unspent, so the operator's Refresh still runs in full.
        Assert.Null((await useCase.Handle(force: true)).RetryAfter);
    }

    /// <summary>
    /// A live skip is not a synchronization, so it records no freshness either: were it to, a
    /// business that stopped being live would then be told its sales were current on the strength of a
    /// run that never happened.
    /// </summary>
    [Fact]
    public async Task A_live_business_records_no_recent_sync()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);

        Assert.True((await UseCase(
            SingleMachine(),
            clock: clock,
            freshness: freshness,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: Polling()).Handle()).LiveViaSqs);

        Assert.Null(freshness.FindRecentSync(
            Inventory.Domain.Tenancy.BusinessId.From(TestSalesSync.BusinessId), credentialRevision: 1));
    }

    /// <summary>
    /// A business nobody has recorded a sender for, and one whose sender is switched off: neither is
    /// live, both synchronize exactly as they did before, and the reason says which.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_sender_runs_as_today_and_reports_the_reason()
    {
        var nayax = SingleMachine();

        var outcome = await UseCase(nayax, readerHealth: Polling()).Handle();

        Assert.False(outcome.LiveViaSqs);
        Assert.Equal(NayaxSqsNotLiveReason.NoSender, outcome.SqsNotLiveReason);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_disabled_sender_runs_as_today_and_reports_the_reason()
    {
        var nayax = SingleMachine();
        var sqs = FakeNayaxSqsLiveStatusStore.Disabled(Now.AddMinutes(-30));

        var outcome = await UseCase(nayax, sqsStatus: sqs, readerHealth: Polling()).Handle();

        Assert.False(outcome.LiveViaSqs);
        Assert.Equal(NayaxSqsNotLiveReason.SenderDisabled, outcome.SqsNotLiveReason);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The issue's own case: a poll three minutes old is not live. The reader may have been working
    /// perfectly a moment ago, but nothing can say it is working now.
    /// </summary>
    [Fact]
    public async Task A_poll_three_minutes_old_is_not_live()
    {
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(Now.AddMinutes(-3));
        var nayax = SingleMachine();

        var outcome = await UseCase(
            nayax,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: health).Handle();

        Assert.Equal(NayaxSqsNotLiveReason.ReaderNotPolling, outcome.SqsNotLiveReason);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// And one failed receive is not live, even with a successful poll seconds old: the count is
    /// reset by the next successful poll, so any non-zero value is a current statement that the queue
    /// is not answering.
    /// </summary>
    [Fact]
    public async Task One_poll_failure_is_not_live()
    {
        var health = Polling();
        health.RecordPollFailure();
        var nayax = SingleMachine();

        var outcome = await UseCase(
            nayax,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: health).Handle();

        Assert.Equal(NayaxSqsNotLiveReason.ReaderFailing, outcome.SqsNotLiveReason);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The 150-minute reconciliation limit at the two instants the issue names, through the use case
    /// rather than only through the rule: 149 minutes is live and reads nothing, 151 is not and reads
    /// everything.
    /// </summary>
    [Theory]
    [InlineData(149, true)]
    [InlineData(151, false)]
    public async Task The_reconciliation_limit_decides_the_on_load_read(int minutesAgo, bool isLive)
    {
        var nayax = SingleMachine();
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-minutesAgo));

        var outcome = await UseCase(nayax, sqsStatus: sqs, readerHealth: Polling()).Handle();

        Assert.Equal(isLive, outcome.LiveViaSqs);
        nayax.Verify(
            x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()),
            isLive ? Times.Never() : Times.Once());

        if (!isLive)
        {
            Assert.Equal(NayaxSqsNotLiveReason.ReconciliationStale, outcome.SqsNotLiveReason);
        }
    }

    /// <summary>
    /// The issue's worked example, as the Dashboard experiences it: a reconciliation succeeds at
    /// minute 0 and the runs due at 60 and 120 are both missed. The business is still live at 149 and
    /// stale at 150 - a 30-minute grace after the <em>second</em> missed run.
    /// </summary>
    [Theory]
    [InlineData(60, true)]
    [InlineData(120, true)]
    [InlineData(149, true)]
    [InlineData(150, false)]
    public async Task The_worked_example_of_two_missed_hourly_runs(int minutesSinceSuccess, bool isLive)
    {
        var succeededAt = Now;
        var clock = new FakeClock(succeededAt.AddMinutes(minutesSinceSuccess));
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(clock.UtcNow.AddSeconds(-10));

        var outcome = await UseCase(
            SingleMachine(),
            clock: clock,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(succeededAt),
            readerHealth: health).Handle();

        Assert.Equal(isLive, outcome.LiveViaSqs);
    }

    /// <summary>
    /// A business whose reconciliation has never fully succeeded is stale, not live: the column is
    /// written only by a run that did everything (issue #1104), so "never" means nothing has ever
    /// verified this business's queue against the provider's own record.
    /// </summary>
    [Fact]
    public async Task A_business_whose_reconciliation_has_never_succeeded_is_not_live()
    {
        var nayax = SingleMachine();
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(reconciliationSucceededAtUtc: null);

        var outcome = await UseCase(nayax, sqsStatus: sqs, readerHealth: Polling()).Handle();

        Assert.Equal(NayaxSqsNotLiveReason.ReconciliationStale, outcome.SqsNotLiveReason);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// <strong>A forced Refresh is unaffected.</strong> It makes no live decision at all - it is
    /// asking for a <c>lastSales</c> read - so a live business's Refresh reads every machine exactly
    /// as it does today, and the response carries no live claim and no reason.
    /// </summary>
    [Fact]
    public async Task A_forced_sync_reads_lastSales_even_for_a_live_business()
    {
        var nayax = SingleMachine();
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30));
        var useCase = UseCase(nayax, sqsStatus: sqs, readerHealth: Polling());

        var outcome = await useCase.Handle(force: true);

        Assert.False(outcome.LiveViaSqs);
        Assert.Null(outcome.SqsNotLiveReason);
        Assert.Null(outcome.LastSqsMessageReceivedAtUtc);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()), Times.Once);

        // And it never asked the sender row anything: no decision was made to need it.
        Assert.Equal(0, sqs.Reads);
    }

    /// <summary>
    /// A forced run is still rationed, exactly as before: the live skip changes what an unforced load
    /// does and nothing about the Refresh control's own bound.
    /// </summary>
    [Fact]
    public async Task A_forced_sync_is_still_subject_to_the_run_limit_for_a_live_business()
    {
        var clock = new FakeClock(Now);
        var useCase = UseCase(
            SingleMachine(),
            clock: clock,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: Polling());

        for (var run = 0; run < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.Null((await useCase.Handle(force: true)).RetryAfter);
        }

        Assert.NotNull((await useCase.Handle(force: true)).RetryAfter);
    }

    /// <summary>
    /// <strong>The refusals are resolved first, in the order issue #843 asserts.</strong> A business
    /// whose connection is missing or needs attention gets exactly that refusal even while every live
    /// condition holds: an unusable connection is never told its sales are live, and nothing about the
    /// queue is read on the way to the refusal.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_refused_connection_is_refused_even_for_a_live_business(
        NayaxConnectionStatus status)
    {
        var nayax = SingleMachine();
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30));

        var refusal = await Assert.ThrowsAsync<NayaxNotConnectedException>(() => UseCase(
            nayax,
            credentials: FakeNayaxOperationCredentials.Refusing(status),
            sqsStatus: sqs,
            readerHealth: Polling()).Handle());

        Assert.Equal(status, refusal.Status);
        Assert.Equal(0, sqs.Reads);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// And a support session, which resolves no Nayax credentials at all (issue #551), is refused
    /// before the live decision too - not answered "live" about a business whose data it may read.
    /// </summary>
    [Fact]
    public async Task A_support_session_is_refused_before_the_live_decision()
    {
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30));

        await Assert.ThrowsAsync<SupportSessionNayaxUnavailableException>(() => UseCase(
            SingleMachine(),
            credentials: FakeNayaxOperationCredentials.InSupportSession(),
            sqsStatus: sqs,
            readerHealth: Polling()).Handle());

        Assert.Equal(0, sqs.Reads);
    }

    /// <summary>
    /// <strong>After a restart the business is not live until the first successful poll.</strong> The
    /// health record is in memory and empty when the process starts, which is a true statement - the
    /// reader has not polled yet - so the Dashboard falls back to today's sync in the meantime and
    /// starts skipping once the reader answers. The durable reconciliation instant is unchanged
    /// across the restart, which is why the first poll is the only thing the business is waiting for.
    /// </summary>
    [Fact]
    public async Task After_a_restart_a_business_is_live_only_once_the_reader_has_polled()
    {
        // A fresh process: one new health record, nothing recorded on it.
        var health = new NayaxSqsReaderHealth();
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30));
        var nayax = SingleMachine();
        var useCase = UseCase(nayax, sqsStatus: sqs, readerHealth: health);

        var beforeFirstPoll = await useCase.Handle();

        Assert.False(beforeFirstPoll.LiveViaSqs);
        Assert.Equal(NayaxSqsNotLiveReason.ReaderNotPolling, beforeFirstPoll.SqsNotLiveReason);
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);

        health.RecordSuccessfulPoll(Now.AddSeconds(-5));

        // A second unforced load, with a freshness memory of its own so the run above cannot be what
        // makes this one skip: the live decision is what is being asserted, and it comes first.
        var afterFirstPoll = await UseCase(
            SingleMachine(), sqsStatus: sqs, readerHealth: health).Handle();

        Assert.True(afterFirstPoll.LiveViaSqs);
    }

    /// <summary>
    /// The live decision is made from one reading of each half: the sender row is read once per
    /// unforced call, beside the health record, so a poll landing between two reads cannot produce a
    /// state the system was never in.
    /// </summary>
    [Fact]
    public async Task An_unforced_call_reads_the_sender_row_once()
    {
        var sqs = FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30));

        await UseCase(SingleMachine(), sqsStatus: sqs, readerHealth: Polling()).Handle();

        Assert.Equal(1, sqs.Reads);
    }

    /// <summary>A reader that polled successfully moments ago, with no failures.</summary>
    private static NayaxSqsReaderHealth Polling()
    {
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(Now.AddSeconds(-10));
        return health;
    }

    private static SyncLatestNayaxSales UseCase(
        Mock<INayaxLynxClient> nayax,
        Mock<ILatestNayaxSalesStore>? store = null,
        FakeClock? clock = null,
        NayaxSalesSyncFreshness? freshness = null,
        FakeNayaxOperationCredentials? credentials = null,
        NayaxSalesSyncRateLimiter? limiter = null,
        FakeNayaxSqsLiveStatusStore? sqsStatus = null,
        NayaxSqsReaderHealth? readerHealth = null)
    {
        var sharedClock = clock ?? new FakeClock(Now);
        return new SyncLatestNayaxSales(
            nayax.Object,
            (store ?? TestSalesSync.EmptyStoreMock()).Object,
            limiter ?? new NayaxSalesSyncRateLimiter(sharedClock),
            freshness ?? new NayaxSalesSyncFreshness(sharedClock),
            new NayaxSalesSyncLock(),
            credentials ?? new FakeNayaxOperationCredentials(),
            new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(TestSalesSync.BusinessId),
            new FakeProviderDataCoverageStore(),
            sqsStatus ?? new FakeNayaxSqsLiveStatusStore(),
            readerHealth ?? new NayaxSqsReaderHealth(),
            sharedClock);
    }

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
}
