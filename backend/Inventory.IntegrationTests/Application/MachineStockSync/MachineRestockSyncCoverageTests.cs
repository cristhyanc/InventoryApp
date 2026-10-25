using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Sync Restock successful-fetch coverage of issue #891, over the real
/// <see cref="EfMachineRestockSyncCoverageStore"/> and relational SQLite.
///
/// <para>Relational rather than InMemory deliberately: the write under test is a single conditional
/// <c>UPDATE ... WHERE</c>, and its whole value is that the comparison and the write cannot be
/// separated. An InMemory test would prove the adapter's C# and nothing about the statement SQLite
/// actually runs, which is exactly the part a concurrent sync depends on.</para>
///
/// <para>Two businesses throughout, because this adds a column a reminder is built from and the
/// counts beside it are read with a provider's machine number - a number two operators may
/// legitimately share.</para>
/// </summary>
public class MachineRestockSyncCoverageTests
{
    /// <summary>The machine's Nayax number: what the provider's alerts and stored events carry.</summary>
    private const long NayaxMachineId = 900;

    /// <summary>
    /// The same machine's local id, deliberately a different number (issue #741), so no test can
    /// pass by confusing a local key with a provider's.
    /// </summary>
    private const long LocalMachineId = 31;

    /// <summary>A second business, whose own machine shares the provider number 900.</summary>
    private const int SellerBusinessId = 1;
    private const int OtherBusinessId = 2;
    private const long OtherLocalMachineId = 71;

    private const long ProductId = 200;
    private const int Mdb = 13;
    private const string ProductName = "25g Nobby's Beef Jerky Hot";

    private static readonly MachineIdentity Machine = new(LocalMachineId, NayaxMachineId);
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SyncStartedAt = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    #region Recording a successful fetch

    [Fact]
    public async Task A_fully_successful_sync_records_its_start_instant_and_credential_revision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            SeedCostedProduct(db);
            await db.SaveChangesAsync();

            await Sync(db, AlertsReturning(StockAlert(1, $"Adjusted Stock, Product MDB: {Mdb} | {ProductName} | 2")),
                    startedAtUtc: SyncStartedAt, credentialRevision: 4)
                .Handle(Machine, CancellationToken.None);
        }

        var coverage = await CoverageOfAsync(options, LocalMachineId);

        // The operation's start instant, not the moment the row happened to be written: a fetch can
        // only have accounted for alerts that already existed when it began.
        Assert.Equal(SyncStartedAt, coverage.StartedAtUtc);
        Assert.Equal(4, coverage.CredentialRevision);
    }

    /// <summary>An empty answer from Nayax is an answer: the fetch succeeded.</summary>
    [Fact]
    public async Task A_successful_sync_that_found_no_alert_still_records_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            await Sync(db, AlertsReturning(), startedAtUtc: SyncStartedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);
        }

        Assert.Equal(SyncStartedAt, (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);
    }

    /// <summary>
    /// A fetch that threw accounted for nothing, so it claims nothing. The machine stays "no
    /// recorded sync", which keeps the reminder warning rather than presenting a failure as
    /// freshness.
    /// </summary>
    [Fact]
    public async Task A_failed_alert_read_records_no_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            var failing = new Mock<INayaxLynxClient>();
            failing.Setup(client => client.GetMachineLastAlertsAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Nayax is unavailable."));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Sync(db, failing, startedAtUtc: SyncStartedAt, credentialRevision: 1)
                    .Handle(Machine, CancellationToken.None));
        }

        await using var verify = Scoped(options);
        Assert.Null(
            (await verify.Machines.AsNoTracking().SingleAsync(machine => machine.Id == LocalMachineId))
                .LastSuccessfulRestockSyncStartedAtUtc);
    }

    /// <summary>
    /// The coverage write is the last thing the workflow does and is not allowed to cost the
    /// operator their reconciliation: the preview still comes back, and the unrecorded coverage
    /// simply leaves the reminder warning.
    /// </summary>
    [Fact]
    public async Task A_failure_to_record_coverage_leaves_the_sync_and_its_preview_intact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using var db = Scoped(options);
        SeedCostedProduct(db);
        await db.SaveChangesAsync();

        var coverage = new FakeMachineRestockSyncCoverageStore
        {
            WriteFailure = new InvalidOperationException("the coverage column is unavailable"),
        };

        var preview = await Sync(
                db,
                AlertsReturning(StockAlert(1, $"Adjusted Stock, Product MDB: {Mdb} | {ProductName} | 2")),
                startedAtUtc: SyncStartedAt,
                credentialRevision: 1,
                coverage: coverage)
            .Handle(Machine, CancellationToken.None);

        Assert.Equal(1, preview.NewEventCount);
        Assert.Single(preview.Events);

        // And the alert really was imported: the failure cost nothing but the claim.
        Assert.Single(await db.NayaxMachineStockEvents.AsNoTracking().ToListAsync());
    }

    #endregion

    #region The monotonic conditional write

    [Fact]
    public async Task A_later_sync_at_the_same_revision_advances_the_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, default));
        Assert.True(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddMinutes(10), 3, default));

        Assert.Equal(SyncStartedAt.AddMinutes(10), (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);
    }

    /// <summary>
    /// Two concurrent syncs: the one that started earlier must not move the coverage backwards when
    /// it finishes last, because it can only have accounted for a subset of what the later one saw.
    /// </summary>
    [Fact]
    public async Task An_earlier_sync_finishing_last_does_not_move_the_coverage_backwards()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, default));

        Assert.False(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddMinutes(-10), 3, default));
        Assert.False(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, default));

        Assert.Equal(SyncStartedAt, (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);
    }

    /// <summary>
    /// New credentials supersede everything the previous token proved, so the first success under
    /// them wins even though it started earlier than the last success under the old ones.
    /// </summary>
    [Fact]
    public async Task A_higher_credential_revision_advances_the_coverage_whatever_its_instant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, default));
        Assert.True(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddMinutes(-30), 4, default));

        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt.AddMinutes(-30), coverage.StartedAtUtc);
        Assert.Equal(4, coverage.CredentialRevision);
    }

    /// <summary>
    /// The credential-replacement case the reminder's honesty rests on: a sync that began under
    /// revision 3 and finished after the Owner stored a new token must not mark revision 4 fresh.
    /// </summary>
    [Fact]
    public async Task A_completion_under_a_superseded_revision_records_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 4, default));

        Assert.False(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddHours(1), 3, default));

        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt, coverage.StartedAtUtc);
        Assert.Equal(4, coverage.CredentialRevision);
    }

    /// <summary>
    /// The same case end to end: the credential revision the sync binds its claim to is the one
    /// read before its first Nayax call, so a save that lands while it runs leaves the coverage
    /// unrecorded rather than wrongly advanced.
    /// </summary>
    [Fact]
    public async Task A_credential_saved_during_a_sync_leaves_that_syncs_coverage_unrecorded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            // The newer credentials already recorded a successful sync of their own.
            Assert.True(
                await new EfMachineRestockSyncCoverageStore(db)
                    .TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 5, default));

            // This run authenticated under revision 4, the token that has since been replaced.
            await Sync(db, AlertsReturning(), startedAtUtc: SyncStartedAt.AddHours(2), credentialRevision: 4)
                .Handle(Machine, CancellationToken.None);
        }

        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt, coverage.StartedAtUtc);
        Assert.Equal(5, coverage.CredentialRevision);
    }

    /// <summary>
    /// The machine of another business is not this business's machine, whatever its local id: the
    /// write is scoped by the central tenant filter and matches nothing.
    /// </summary>
    [Fact]
    public async Task Coverage_cannot_be_recorded_against_another_businesss_machine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            Assert.False(
                await new EfMachineRestockSyncCoverageStore(db)
                    .TryRecordSuccessfulSyncAsync(OtherLocalMachineId, SyncStartedAt, 1, default));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Null(
            (await verify.Machines.AsNoTracking().SingleAsync(machine => machine.Id == OtherLocalMachineId))
                .LastSuccessfulRestockSyncStartedAtUtc);
    }

    #endregion

    #region The aggregate read

    [Fact]
    public async Task The_aggregate_read_counts_only_unresolved_events_of_provider_named_machines()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var seed = Scoped(options))
        {
            // Three still-unresolved events - a needs-review one, a matched one and a flagged
            // possible duplicate nobody has resolved - and two that are settled.
            seed.NayaxMachineStockEvents.AddRange(
                Event(1, EventTime.AddDays(-9), NayaxStockEventProcessingStatus.Unprocessed),
                Event(2, EventTime.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed),
                Event(3, EventTime, NayaxStockEventProcessingStatus.Unprocessed),
                Event(4, EventTime.AddDays(-20), NayaxStockEventProcessingStatus.Applied),
                Event(
                    5,
                    EventTime.AddDays(-15),
                    NayaxStockEventProcessingStatus.Unprocessed,
                    NayaxDuplicateResolution.ReconciledManually));
            await seed.SaveChangesAsync();
        }

        await using var db = Scoped(options);
        var machines = await new EfMachineRestockSyncCoverageStore(db)
            .GetProviderMachineCoverageAsync(CancellationToken.None);

        // Only the provider-named machine: the one the operator created themselves has no provider
        // restock sync to be overdue for.
        var machine = Assert.Single(machines);
        Assert.Equal(LocalMachineId, machine.LocalMachineId);
        Assert.Equal("Avenue Hotel", machine.MachineName);
        Assert.Equal(3, machine.KnownPendingEventCount);

        // The oldest of the three, as an unambiguous UTC instant - the frontend parses it as one.
        Assert.Equal(EventTime.AddDays(-9), machine.OldestKnownPendingEventAtUtc);
        Assert.Equal(DateTimeKind.Utc, machine.OldestKnownPendingEventAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task A_provider_named_machine_with_nothing_outstanding_is_reported_with_no_pending_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using var db = Scoped(options);
        var machine = Assert.Single(
            await new EfMachineRestockSyncCoverageStore(db).GetProviderMachineCoverageAsync(CancellationToken.None));

        Assert.Equal(0, machine.KnownPendingEventCount);
        Assert.Null(machine.OldestKnownPendingEventAtUtc);
        Assert.Null(machine.LastSuccessfulSyncStartedAtUtc);
        Assert.Null(machine.CredentialRevision);
    }

    /// <summary>
    /// Both halves of the read stay inside one business, even though the two businesses' machines
    /// share provider number 900 - which is exactly the case a join on a provider id would get
    /// wrong.
    /// </summary>
    [Fact]
    public async Task Neither_the_coverage_nor_the_counts_cross_a_business_boundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var other = TestAppDbContext.For(options, OtherBusinessId))
        {
            other.NayaxMachineStockEvents.AddRange(
                Event(11, EventTime, NayaxStockEventProcessingStatus.Unprocessed),
                Event(12, EventTime, NayaxStockEventProcessingStatus.Unprocessed));
            await other.SaveChangesAsync();

            Assert.True(
                await new EfMachineRestockSyncCoverageStore(other)
                    .TryRecordSuccessfulSyncAsync(OtherLocalMachineId, SyncStartedAt, 9, default));
        }

        await using var db = Scoped(options);
        var machine = Assert.Single(
            await new EfMachineRestockSyncCoverageStore(db).GetProviderMachineCoverageAsync(CancellationToken.None));

        Assert.Equal(LocalMachineId, machine.LocalMachineId);
        Assert.Equal(0, machine.KnownPendingEventCount);
        Assert.Null(machine.LastSuccessfulSyncStartedAtUtc);

        // And the other business still sees its own.
        await using var otherRead = TestAppDbContext.For(options, OtherBusinessId);
        var otherMachine = Assert.Single(
            await new EfMachineRestockSyncCoverageStore(otherRead)
                .GetProviderMachineCoverageAsync(CancellationToken.None));

        Assert.Equal(OtherLocalMachineId, otherMachine.LocalMachineId);
        Assert.Equal(2, otherMachine.KnownPendingEventCount);
        Assert.Equal(SyncStartedAt, otherMachine.LastSuccessfulSyncStartedAtUtc);
        Assert.Equal(9, otherMachine.CredentialRevision);
    }

    /// <summary>
    /// A caller whose business could not be resolved reads nothing and writes nothing: the central
    /// filter, not a predicate in this adapter, is what makes that true.
    /// </summary>
    [Fact]
    public async Task A_denied_scope_reads_nothing_and_records_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using var denied = TestAppDbContext.Denied(options);
        var store = new EfMachineRestockSyncCoverageStore(denied);

        Assert.Empty(await store.GetProviderMachineCoverageAsync(CancellationToken.None));
        Assert.False(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 1, default));
    }

    #endregion

    #region Fixtures

    private static async Task<DbContextOptions<AppDbContext>> CreateDatabaseAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var schema = TestAppDbContext.Unrestricted(options))
        {
            await schema.Database.EnsureCreatedAsync();

            schema.Businesses.AddRange(
                new Business
                {
                    Id = SellerBusinessId,
                    Name = "Vending A",
                    Status = BusinessStatus.Active,
                    SalesProvider = ExternalProvider.Nayax,
                    CreatedAtUtc = EventTime,
                },
                new Business
                {
                    Id = OtherBusinessId,
                    Name = "Vending B",
                    Status = BusinessStatus.Active,
                    SalesProvider = ExternalProvider.Nayax,
                    CreatedAtUtc = EventTime,
                });

            schema.Machines.AddRange(
                new LocalMachine
                {
                    BusinessId = SellerBusinessId,
                    Id = LocalMachineId,
                    Name = "Avenue Hotel",
                    ExternalMachineId = NayaxExternalId.From(NayaxMachineId),
                },
                // The operator's own machine: no provider id, so no provider restock sync.
                new LocalMachine
                {
                    BusinessId = SellerBusinessId,
                    Id = LocalMachineId + 1,
                    Name = "Workshop Fridge",
                },
                // Another business's machine, sharing the provider number.
                new LocalMachine
                {
                    BusinessId = OtherBusinessId,
                    Id = OtherLocalMachineId,
                    Name = "Coastal Kiosk",
                    ExternalMachineId = NayaxExternalId.From(NayaxMachineId),
                });

            await schema.SaveChangesAsync();
        }

        return options;
    }

    private static AppDbContext Scoped(DbContextOptions<AppDbContext> options) =>
        TestAppDbContext.For(options, SellerBusinessId);

    private static async Task<(DateTime? StartedAtUtc, int? CredentialRevision)> CoverageOfAsync(
        DbContextOptions<AppDbContext> options, long localMachineId)
    {
        await using var verify = TestAppDbContext.Unrestricted(options);
        var machine = await verify.Machines.AsNoTracking().SingleAsync(row => row.Id == localMachineId);
        return (machine.LastSuccessfulRestockSyncStartedAtUtc, machine.LastSuccessfulRestockSyncCredentialRevision);
    }

    private static SyncMachineStockFromNayax Sync(
        AppDbContext db,
        Mock<INayaxLynxClient> nayax,
        DateTime startedAtUtc,
        int credentialRevision,
        IMachineRestockSyncCoverageStore? coverage = null) =>
        new(
            nayax.Object,
            new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfNayaxProductLinkStore(db),
            new FakeClock(startedAtUtc),
            new FakeNayaxConnectionStore("op", "token", NayaxConnectionStatus.Ready, credentialRevision),
            coverage ?? new EfMachineRestockSyncCoverageStore(db));

    private static Mock<INayaxLynxClient> AlertsReturning(params NayaxMachineAlert[] alerts)
    {
        var mock = new Mock<INayaxLynxClient>();
        mock.Setup(client => client.GetMachineLastAlertsAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alerts.ToList());
        mock.Setup(client => client.GetMachineProductsFreshAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachineProduct { NayaxProductID = ProductId, MDBCode = Mdb, ProductName = ProductName }]);
        return mock;
    }

    private static NayaxMachineAlert StockAlert(long eventLogId, string eventData) => new()
    {
        EventLogId = eventLogId,
        MachineId = NayaxMachineId,
        EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
        EventDescription = "Stock Adjust for Machine",
        EventData = eventData,
        EventDateTimeGmt = EventTime,
        EventDateTimeVmc = DateTime.SpecifyKind(EventTime.AddHours(10), DateTimeKind.Unspecified),
    };

    private static NayaxMachineStockEvent Event(
        long eventLogId,
        DateTime eventDateTimeGmt,
        NayaxStockEventProcessingStatus processingStatus,
        NayaxDuplicateResolution duplicateResolution = NayaxDuplicateResolution.None) => new()
        {
            NayaxEventLogId = eventLogId,
            MachineId = NayaxMachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = eventDateTimeGmt,
            EventDateTimeVmc = DateTime.SpecifyKind(eventDateTimeGmt.AddHours(10), DateTimeKind.Unspecified),
            RawEventData = "Adjusted Stock",
            MatchStatus = NayaxStockEventMatchStatus.NeedsReview,
            ProcessingStatus = processingStatus,
            DuplicateResolution = duplicateResolution,
        };

    private static void SeedCostedProduct(AppDbContext db) =>
        db.Products.Add(new Product
        {
            Id = ProductId,
            ExternalProductId = NayaxExternalId.From(ProductId),
            Name = ProductName,
            QuantityInStock = 20,
            CostingQuantity = 20,
            InventoryValue = 20m,
            AverageUnitCost = 1m,
        });

    #endregion
}
