using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxReconciliation;
using Inventory.Application.SalesSync;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Providers;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.SalesSync;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxReconciliation;

/// <summary>
/// One business's hourly reconciliation (issue #1104): what it compares, what it records, when it
/// skips, and - the question issue #1106 depends on - when it is allowed to say it succeeded.
///
/// <para><strong>The success instant is the thing worth being strict about.</strong> It is the only
/// evidence another feature will trust that a business's sales have been checked against the
/// provider's own record, so these cases assert it in both directions: a run that did everything
/// records the instant it <em>started</em>, and a run that was skipped, that read one machine badly,
/// that could not persist, or that left a pending cost rebuild behind records nothing at all.</para>
///
/// <para>What is <em>not</em> here is the comparison arithmetic and the absence rule, which are pure
/// functions with their own tests (<c>NayaxSaleProviderComparisonPolicyTests</c>,
/// <c>NayaxSaleProviderAbsencePolicyTests</c>), and the write-locked evidence write, which is
/// <c>EfNayaxSaleProviderEvidenceStoreTests</c>'s on a real database.</para>
/// </summary>
public class ReconcileNayaxSalesWithProviderTests
{
    private const int TestBusinessId = 1;
    private const long MachineId = 4200001;
    private const long TransactionId = 4100000001;

    private static readonly DateTime Now = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SaleInstant = Now.AddHours(-1);

    /// <summary>
    /// The ordinary case: the provider agrees, the sale is verified, the evidence is recorded and the
    /// run's own start instant becomes the business's last full success.
    /// </summary>
    [Fact]
    public async Task A_matching_sale_is_verified_and_the_run_records_its_start_instant()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item(settlementValue: 3.5m));

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ItemsRead);
        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.Conflicts);
        Assert.Equal(0, result.NeedsInvestigation);

        Assert.Equal(NayaxSqsVerification.Verified, world.Evidence.StateOf(10));

        var comparison = Assert.Single(world.Evidence.Comparisons);
        Assert.Equal(10, comparison.SaleId);
        Assert.Equal(3.5m, comparison.Provider.SettlementValue);
        Assert.Equal(NayaxSaleFieldComparison.Match, world.Evidence.FieldsOf(10)!.Amount);

        Assert.Equal([Now], world.Success.Recorded);
    }

    /// <summary>
    /// A contradiction about what was sold: the provider's own record carries a different amount for
    /// the same transaction, and both values were present.
    /// </summary>
    [Fact]
    public async Task A_contradicting_sale_conflicts_and_the_run_still_succeeds()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item(settlementValue: 9.99m));

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.Verified);
        Assert.Equal(NayaxSqsVerification.Conflict, world.Evidence.StateOf(10));

        // A conflict is a finding about a sale, not a failure of the run: the run did exactly what it
        // was for, so it records its success and #1106 may trust it.
        Assert.Equal([Now], world.Success.Recorded);
    }

    /// <summary>
    /// A transaction the response did not return, bracketed by what it did return and outside the
    /// receipt grace: one absence row, and the sale is flagged - never treated as proven fake.
    /// </summary>
    [Fact]
    public async Task A_bracketed_missing_sale_writes_an_absence_row_and_needs_investigation()
    {
        var world = new World();
        world.WithQueueSale(
            saleId: 10,
            settlementValue: 3.5m,
            transactionId: TransactionId,
            saleInstant: SaleInstant,
            receivedViaSqsAtUtc: SaleInstant);

        // The response returns two other transactions, one on each side of the stored sale's instant.
        world.WithLastSales(
            Item(transactionId: 7001, saleInstant: SaleInstant.AddHours(-1)),
            Item(transactionId: 7002, saleInstant: SaleInstant.AddMinutes(10)));

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.AbsenceRows);
        Assert.Equal(1, result.NeedsInvestigation);
        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, world.Evidence.StateOf(10));

        var absence = Assert.Single(world.Evidence.Absences);
        Assert.Equal(MachineId, absence.Window.MachineId);
        Assert.Equal(2, absence.Window.ReturnedItemCount);
        Assert.Equal(SaleInstant.AddHours(-1), absence.Window.EarliestReturnedInstantUtc);
        Assert.Equal(SaleInstant.AddMinutes(10), absence.Window.LatestReturnedInstantUtc);
        Assert.Equal(SaleInstant, absence.SaleAuthorizationInstantUtc);
        Assert.True(absence.Observation.Bracketed);
        Assert.True(absence.Observation.OutsideReceiptGrace);
    }

    /// <summary>
    /// A sale that is not bracketed writes nothing and stays <c>Unverified</c>: outside the range the
    /// response reached, the provider said nothing at all.
    /// </summary>
    [Fact]
    public async Task A_missing_sale_that_is_not_bracketed_writes_nothing_and_stays_unverified()
    {
        var world = new World();
        world.WithQueueSale(
            saleId: 10,
            settlementValue: 3.5m,
            saleInstant: SaleInstant,
            receivedViaSqsAtUtc: SaleInstant);

        // Both returned items are newer than the stored sale, so it is past the end of the window.
        world.WithLastSales(
            Item(transactionId: 7001, saleInstant: SaleInstant.AddMinutes(5)),
            Item(transactionId: 7002, saleInstant: SaleInstant.AddMinutes(10)));

        var result = await world.Reconcile();

        Assert.Equal(0, result.AbsenceRows);
        Assert.Empty(world.Evidence.Absences);
        Assert.Equal(NayaxSqsVerification.Unverified, world.Evidence.StateOf(10));
        Assert.Equal([Now], world.Success.Recorded);
    }

    /// <summary>
    /// A sale the queue handed over less than the grace before the run writes nothing: the provider's
    /// own record may simply not show it yet.
    /// </summary>
    [Fact]
    public async Task A_missing_sale_received_inside_the_receipt_grace_writes_nothing()
    {
        var world = new World();
        world.WithQueueSale(
            saleId: 10,
            settlementValue: 3.5m,
            saleInstant: SaleInstant,
            receivedViaSqsAtUtc: Now.AddMinutes(-5));

        world.WithLastSales(
            Item(transactionId: 7001, saleInstant: SaleInstant.AddHours(-1)),
            Item(transactionId: 7002, saleInstant: SaleInstant.AddMinutes(10)));

        var result = await world.Reconcile();

        Assert.Equal(0, result.AbsenceRows);
        Assert.Empty(world.Evidence.Absences);
        Assert.Equal(NayaxSqsVerification.Unverified, world.Evidence.StateOf(10));
    }

    /// <summary>
    /// Running the same hour twice over the same rolling window records nothing the second time: an
    /// hourly run that learnt nothing new writes nothing, and the counts say so.
    /// </summary>
    [Fact]
    public async Task A_second_run_over_the_same_window_records_no_new_evidence()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item(settlementValue: 3.5m));

        Assert.Equal(1, (await world.Reconcile()).Verified);

        var second = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Succeeded, second.Outcome);
        Assert.Equal(0, second.Verified);
        Assert.Equal(1, second.EvidenceSkipped);
        Assert.Single(world.Evidence.Comparisons);

        // Both runs succeeded, so the instant moved on: the sales really are checked as of the second
        // run too.
        Assert.Equal([Now, Now], world.Success.Recorded);
    }

    /// <summary>
    /// Sales that <c>lastSales</c> returns but the queue never delivered are added as today, with no
    /// verification state at all - there is no queue delivery to verify.
    /// </summary>
    [Fact]
    public async Task A_sale_only_lastSales_delivered_is_added_and_given_no_state()
    {
        var world = new World();
        world.WithLastSales(Item(transactionId: 7001));
        world.Store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(
                new Dictionary<long, DateTime>(), InsertedCount: 1));

        var result = await world.Reconcile();

        Assert.Equal(1, result.SalesAdded);
        Assert.Equal(0, result.Verified);
        Assert.Empty(world.Evidence.Comparisons);
        Assert.Empty(world.Evidence.Absences);
    }

    /// <summary>
    /// <strong>The lock, taken with try-acquire.</strong> A business whose sync is already running is
    /// skipped for this hour: no Nayax call, no write, no drain and the success instant untouched.
    /// </summary>
    [Fact]
    public async Task A_business_whose_sync_is_already_running_is_skipped_entirely()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item());

        using var held = await world.SyncLock.TryAcquireAsync(
            BusinessId.From(TestBusinessId), CancellationToken.None);
        Assert.True(held.Acquired);

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.SkippedSyncRunning, result.Outcome);
        Assert.False(result.Succeeded);

        world.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Store.Verify(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(world.Evidence.Written);
        Assert.Empty(world.Success.Recorded);
    }

    /// <summary>
    /// The connection, plan and support-session gates, applied through the same port the
    /// request-driven sync uses. A refused business is skipped - and no Nayax call is made, so a
    /// business with an unusable connection costs the sweep nothing.
    /// </summary>
    [Fact]
    public async Task A_business_whose_connection_is_unusable_is_skipped()
    {
        var world = new World { Credentials = FakeNayaxOperationCredentials.Refusing() };
        world.WithLastSales(Item());

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.SkippedGateRefused, result.Outcome);
        world.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(world.Success.Recorded);
    }

    [Fact]
    public async Task A_business_whose_plan_does_not_include_the_sales_provider_is_skipped()
    {
        var world = new World
        {
            Credentials = FakeNayaxOperationCredentials.Refusing(),
            CredentialFailure = NayaxNotConnectedException.PlanRequired(SubscriptionPlan.Pro),
        };
        world.WithLastSales(Item());

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.SkippedGateRefused, result.Outcome);
        world.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(world.Success.Recorded);
    }

    [Fact]
    public async Task A_scope_inside_a_support_session_is_skipped()
    {
        var world = new World { Credentials = FakeNayaxOperationCredentials.InSupportSession() };
        world.WithLastSales(Item());

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.SkippedGateRefused, result.Outcome);
        world.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A refused run is skipped until the next hour. The run limit of issue #681 still applies to the
    /// hourly run, so a business whose Dashboard has been refreshed hard simply waits an hour.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_run_is_skipped_and_records_no_success()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item());

        for (var spend = 0; spend < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; spend++)
        {
            Assert.True(world.Limiter.TryClaim(BusinessId.From(TestBusinessId), out _));
        }

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.SkippedRateLimited, result.Outcome);
        world.Nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(world.Evidence.Written);
        Assert.Empty(world.Success.Recorded);
    }

    /// <summary>
    /// Every pending inventory-cost rebuild is drained <em>first</em>: a replay that failed hours ago
    /// is a durable row, and a run like this is what retries it. It happens even for a run the
    /// allowance then refuses, because it makes no Nayax call.
    /// </summary>
    [Fact]
    public async Task The_run_drains_the_pending_rebuilds_before_it_reads_anything()
    {
        var world = new World();
        world.WithLastSales(Item());

        var calls = new List<string>();
        world.Nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls.Add("machines");
                return [new NayaxMachine { MachineID = MachineId, MachineName = "Machine A" }];
            });
        world.Store.Setup(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls.Add("drain");
                return new InventoryCostRebuildDrainResult([], []);
            });

        await world.Reconcile();

        // Drain, then read, then drain again - the second one being the sync's own - and a last drain
        // after the comparison, for the rebuilds a counting change staged (issue #1105).
        Assert.Equal(["drain", "machines", "drain", "drain"], calls);
    }

    /// <summary>
    /// One machine's read failing leaves the success instant alone: the run did not account for every
    /// machine, so it cannot claim to have reconciled the business.
    /// </summary>
    [Fact]
    public async Task One_machine_read_failing_records_no_success()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.Nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxMachine { MachineID = MachineId, MachineName = "Machine A" },
                new NayaxMachine { MachineID = MachineId + 1, MachineName = "Machine B" },
            ]);
        world.Nayax.Setup(x => x.GetMachineLastSalesAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Item()]);
        world.Nayax.Setup(x => x.GetMachineLastSalesAsync(MachineId + 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Nayax unavailable"));

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Failed, result.Outcome);
        Assert.Equal(nameof(InvalidOperationException), result.Failure);
        Assert.Empty(world.Success.Recorded);

        // Nothing was persisted either, which is the existing all-or-nothing batch rule (issue #730).
        world.Store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_persistence_failure_records_no_success()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item());
        world.Store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Failed, result.Outcome);
        Assert.Empty(world.Success.Recorded);
        Assert.Empty(world.Evidence.Written);
    }

    /// <summary>
    /// A rebuild left pending records no success: the run created or inherited work it did not
    /// finish, so what a later feature would read as "checked" is not true yet.
    /// </summary>
    [Fact]
    public async Task A_rebuild_left_pending_records_no_success()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item());

        var drains = 0;
        world.Store.Setup(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++drains == 1
                // Every drain reports the failure. The sync completes regardless - the sales are
                // stored - but the reconciliation's own last drain still sees it and records no success.
                ? new InventoryCostRebuildDrainResult([99], ["Product 99: unreplayable history."])
                : new InventoryCostRebuildDrainResult([99], ["Product 99: unreplayable history."]));

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Failed, result.Outcome);
        Assert.Equal(nameof(InventoryCostDataQualityException), result.Failure);
        Assert.Empty(world.Success.Recorded);
    }

    /// <summary>
    /// An evidence write failing records no success: a run whose observations were not all recorded
    /// has not reconciled anything, however much of it read correctly.
    /// </summary>
    [Fact]
    public async Task An_evidence_write_failing_records_no_success()
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item());
        world.Evidence.WriteFailure = new InvalidOperationException("write lock unavailable");

        var result = await world.Reconcile();

        Assert.Equal(NayaxSalesReconciliationOutcome.Failed, result.Outcome);
        Assert.Empty(world.Success.Recorded);
    }

    /// <summary>
    /// The lease is released whatever happened, so a failing business does not hold its own lock until
    /// the process restarts.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_lease_is_released_whether_the_run_succeeded_or_failed(bool failing)
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m);
        world.WithLastSales(Item());

        if (failing)
        {
            world.Evidence.WriteFailure = new InvalidOperationException("write lock unavailable");
        }

        await world.Reconcile();

        using var after = await world.SyncLock.TryAcquireAsync(
            BusinessId.From(TestBusinessId), CancellationToken.None);
        Assert.True(after.Acquired);
    }

    /// <summary>
    /// Coverage is the existing policy's and nothing else's (issue #730): an hourly run records
    /// exactly what a Dashboard-triggered run over the same data would, because it is the same code
    /// path with the same bound - the latest per-machine window start to the instant reading began.
    /// </summary>
    [Fact]
    public async Task An_hourly_run_records_exactly_the_coverage_a_dashboard_run_would()
    {
        var hourly = new World();
        hourly.WithLastSales(Item(saleInstant: Now.AddMinutes(-30)));
        await hourly.Reconcile();

        var dashboard = new World();
        dashboard.WithLastSales(Item(saleInstant: Now.AddMinutes(-30)));
        await dashboard.Sync().Handle(force: true);

        var byHourly = Assert.Single(hourly.Coverage.Recorded);
        var byDashboard = Assert.Single(dashboard.Coverage.Recorded);

        Assert.Equal(ProviderDataSource.SalesSync, byHourly.Source);
        Assert.Equal(byDashboard.Source, byHourly.Source);
        Assert.Equal(byDashboard.FromUtc, byHourly.FromUtc);
        Assert.Equal(byDashboard.ToUtc, byHourly.ToUtc);
        Assert.Equal(Now.AddMinutes(-30), byHourly.FromUtc);
        Assert.Equal(Now, byHourly.ToUtc);
    }

    /// <summary>
    /// A run with an undated item claims no coverage, exactly as the request-driven run does: the
    /// store does not import such an item at all, so the batch did not account for its own range.
    /// </summary>
    [Fact]
    public async Task A_run_with_an_undated_item_claims_no_coverage()
    {
        var world = new World();
        var undated = Item();
        undated.AuthorizationDateTimeGmt = null;
        world.WithLastSales(undated);

        await world.Reconcile();

        Assert.Empty(world.Coverage.Recorded);
    }

    /// <summary>
    /// A machine whose response has no range brackets nothing, so nothing is even read for it: an
    /// empty or single-item response has no interior for a sale to be inside.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_response_with_no_range_asks_no_absence_question(int returnedItems)
    {
        var world = new World();
        world.WithQueueSale(saleId: 10, settlementValue: 3.5m, transactionId: 9999);
        world.WithLastSales(returnedItems == 0 ? [] : [Item(transactionId: 7001)]);

        await world.Reconcile();

        Assert.Empty(world.Evidence.Bracketing);
        Assert.Empty(world.Evidence.Absences);
        Assert.Equal(NayaxSqsVerification.Unverified, world.Evidence.StateOf(10));
    }

    private static NayaxLastSalesReport Item(
        long transactionId = TransactionId,
        decimal settlementValue = 3.5m,
        DateTime? saleInstant = null,
        long machineId = MachineId) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = machineId,
            MachineName = "Machine A",
            SettlementValue = settlementValue,
            PaymentMethod = "Credit Card",
            ProductName = "Fictional Spring Water 600ml",
            NayaxProductId = null,
            CurrencyCode = "AUD",
            AuthorizationDateTimeGmt = new DateTimeOffset(saleInstant ?? SaleInstant, TimeSpan.Zero),
        };

    /// <summary>
    /// The use case with the real sync, the real limiter, the real lock and the real transitions
    /// behind the evidence fake - so what these cases assert is the use case's own order and its own
    /// success rule, not a re-implementation of either.
    /// </summary>
    private sealed class World
    {
        public World()
        {
            Nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachine { MachineID = MachineId, MachineName = "Machine A" }]);
            Nayax.Setup(x => x.GetMachineLastSalesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            Store = TestSalesSync.EmptyStoreMock();
        }

        public FakeClock Clock { get; } = new(Now);

        public Mock<INayaxLynxClient> Nayax { get; } = new();

        public Mock<ILatestNayaxSalesStore> Store { get; }

        public FakeNayaxSaleProviderEvidenceStore Evidence { get; } = new();

        public FakeNayaxReconciliationSuccessStore Success { get; } = new();

        public FakeProviderDataCoverageStore Coverage { get; } = new();

        public NayaxSalesSyncLock SyncLock { get; } = new(TimeSpan.FromMilliseconds(20));

        public NayaxSalesSyncRateLimiter Limiter => _limiter ??= new NayaxSalesSyncRateLimiter(Clock);

        public FakeNayaxOperationCredentials Credentials { get; init; } = new();

        /// <summary>
        /// A specific refusal for the credential gate, for the plan case - the fake's own factory
        /// only makes the connection-status one.
        /// </summary>
        public Exception? CredentialFailure { get; init; }

        private NayaxSalesSyncRateLimiter? _limiter;

        public void WithQueueSale(
            long saleId,
            decimal settlementValue,
            long transactionId = TransactionId,
            DateTime? saleInstant = null,
            DateTime? receivedViaSqsAtUtc = null) =>
            Evidence.WithQueueSale(
                saleId,
                transactionId,
                new NayaxStoredSaleValues(
                    MachineId, settlementValue, saleInstant ?? SaleInstant, null, null),
                receivedViaSqsAtUtc ?? saleInstant ?? SaleInstant);

        public void WithLastSales(params NayaxLastSalesReport[] sales) =>
            Nayax.Setup(x => x.GetMachineLastSalesAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sales.ToList());

        public SyncLatestNayaxSales Sync() => new(
            Nayax.Object,
            Store.Object,
            Limiter,
            new NayaxSalesSyncFreshness(Clock),
            SyncLock,
            CredentialProvider(),
            new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(TestBusinessId),
            Coverage,
            // The hourly run always forces, so it makes no live decision at all (issue #1106): these
            // two are never consulted on this path, and the empty defaults say so.
            new InventoryApi.Tests.Application.NayaxSqs.FakeNayaxSqsLiveStatusStore(),
            new Inventory.Application.NayaxSqs.NayaxSqsReaderHealth(),
            Clock);

        public Task<NayaxSalesReconciliationResult> Reconcile() =>
            new ReconcileNayaxSalesWithProvider(
                    Sync(),
                    Store.Object,
                    Evidence,
                    Success,
                    CredentialProvider(),
                    SyncLock,
                    new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(TestBusinessId),
                    Clock)
                .Handle(CancellationToken.None);

        private INayaxRequestCredentialProvider CredentialProvider() =>
            CredentialFailure is { } failure
                ? new ThrowingCredentials(failure)
                : Credentials;

        private sealed class ThrowingCredentials : INayaxRequestCredentialProvider
        {
            private readonly Exception _failure;

            public ThrowingCredentials(Exception failure) => _failure = failure;

            public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken) =>
                throw _failure;

            public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
