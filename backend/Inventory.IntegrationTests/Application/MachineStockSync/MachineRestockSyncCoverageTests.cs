using System.Data.Common;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Sync Restock successful-fetch coverage of issue #891, over the real
/// <see cref="EfMachineRestockSyncCoverageStore"/> and relational SQLite.
///
/// <para>Relational rather than InMemory deliberately: the write under test is a single conditional
/// <c>UPDATE ... WHERE</c>, and its whole value is that the comparisons and the write cannot be
/// separated. An InMemory test would prove the adapter's C# and nothing about the statement SQLite
/// actually runs, which is exactly the part a concurrent sync depends on.</para>
///
/// <para>The credential half is relational for a second reason: the fence is a condition over the
/// business's real <c>BusinessNayaxConnection</c> row, so the credentials these tests replace are
/// replaced through the real <see cref="EfNayaxConnectionStore"/> - with real encryption and the
/// real revision increment - and the revision an operation's own Nayax request authenticates under
/// is resolved by the real <see cref="NayaxRequestCredentialProvider"/> at the moment that request
/// happens, not asserted from the use case's own read. A test double could not have told the two
/// revisions apart, which is the whole point.</para>
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

    /// <summary>The other business's machine, named by the same provider number.</summary>
    private static readonly MachineIdentity OtherMachine = new(OtherLocalMachineId, NayaxMachineId);

    /// <summary>This test's own throwaway token-protection key id; nothing here is a real secret.</summary>
    private const string ActiveKeyId = "2026-10";

    private static readonly MachineIdentity Machine = new(LocalMachineId, NayaxMachineId);
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A pending window start earlier than every event here, for the tests about which events are
    /// unresolved rather than about the window itself (issue #948).
    /// </summary>
    private static readonly DateTime AllTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SyncStartedAt = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A pending window that starts after every event these tests import, for the direct store
    /// calls whose subject is the monotonic and credential conditions rather than the "nothing left
    /// to sync" one (issue #1018).
    /// </summary>
    private static readonly DateTime AfterEveryEvent = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    #region Recording a successful fetch

    [Fact]
    public async Task A_fully_successful_sync_records_its_start_instant_and_credential_revision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        // Revision 4 is what the business is really on, so the run authenticates under the
        // credentials its claim names.
        await AtCredentialRevisionAsync(options, SellerBusinessId, 4);

        await using (var db = Scoped(options))
        {
            SeedCostedProduct(db);
            await db.SaveChangesAsync();

            // A negative discrepancy is imported but is never restock work (issue #948), so this run
            // leaves nothing to sync and its claim is recorded (issue #1018).
            await Sync(db, AlertsReturning(StockAlert(1, $"Adjusted Stock, Product MDB: {Mdb} | {ProductName} | -2")),
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

    #region Nothing left to sync (issue #1018)

    /// <summary>
    /// The owner's rule: a machine is synced when nothing is left to sync. A fetch that imports a
    /// refill nobody has applied yet leaves work waiting, so it claims nothing - and still hands
    /// the operator the preview and imports the alert.
    /// </summary>
    [Fact]
    public async Task A_sync_that_leaves_pending_work_records_no_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            SeedCostedProduct(db);
            await db.SaveChangesAsync();

            var preview = await Sync(db, AlertsReturning(StockAlert(1, $"Adjusted Stock, Product MDB: {Mdb} | {ProductName} | 2")),
                    startedAtUtc: SyncStartedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);

            Assert.Equal(1, preview.NewEventCount);
            Assert.Single(preview.Events);
        }

        await using var verify = Scoped(options);
        Assert.Single(await verify.NayaxMachineStockEvents.AsNoTracking().ToListAsync());
        Assert.Null(
            (await verify.Machines.AsNoTracking().SingleAsync(machine => machine.Id == LocalMachineId))
                .LastSuccessfulRestockSyncStartedAtUtc);
    }

    /// <summary>
    /// An event that needs review (here unparsed) is work the operator still has to do, so it
    /// blocks the claim exactly as an unapplied refill does.
    /// </summary>
    [Fact]
    public async Task An_event_needing_review_inside_the_window_records_no_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var seed = Scoped(options))
        {
            seed.NayaxMachineStockEvents.Add(Event(1, SyncStartedAt.AddDays(-2), NayaxStockEventProcessingStatus.Unprocessed));
            await seed.SaveChangesAsync();
        }

        await using (var db = Scoped(options))
        {
            await Sync(db, AlertsReturning(), startedAtUtc: SyncStartedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);
        }

        await using var verify = Scoped(options);
        Assert.Null(
            (await verify.Machines.AsNoTracking().SingleAsync(machine => machine.Id == LocalMachineId))
                .LastSuccessfulRestockSyncStartedAtUtc);
    }

    /// <summary>
    /// The same bounds the reminder counts with: an event older than the 14-day window, a negative
    /// or zero discrepancy, an applied event and one reconciled as already recorded manually are
    /// not work left to sync, so none of them blocks the claim.
    /// </summary>
    [Fact]
    public async Task Events_the_reminder_does_not_count_do_not_block_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        // FakeBusinessCalendar dates in UTC, so the window starts at midnight 14 days before.
        var windowStart = SyncStartedAt.Date.AddDays(-14);

        await using (var seed = Scoped(options))
        {
            seed.NayaxMachineStockEvents.AddRange(
                Event(1, windowStart.AddSeconds(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 6),
                Event(2, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: -2),
                Event(3, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 0),
                Event(4, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Applied, parsedQuantity: 4),
                Event(5, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed,
                    NayaxDuplicateResolution.ReconciledManually, parsedQuantity: 4));
            await seed.SaveChangesAsync();
        }

        await using (var db = Scoped(options))
        {
            await Sync(db, AlertsReturning(), startedAtUtc: SyncStartedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);
        }

        Assert.Equal(SyncStartedAt, (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);
    }

    /// <summary>
    /// Clearing the last pending event and then fetching again - which the Sync Restock dialog does
    /// by itself after every apply and resolve - is what records the machine as synced.
    /// </summary>
    [Fact]
    public async Task The_sync_after_the_last_pending_event_is_cleared_records_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var seed = Scoped(options))
        {
            seed.NayaxMachineStockEvents.Add(
                Event(1, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 3));
            await seed.SaveChangesAsync();
        }

        await using (var db = Scoped(options))
        {
            await Sync(db, AlertsReturning(), startedAtUtc: SyncStartedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);
        }

        await using (var verify = Scoped(options))
        {
            Assert.Null(
                (await verify.Machines.AsNoTracking().SingleAsync(machine => machine.Id == LocalMachineId))
                    .LastSuccessfulRestockSyncStartedAtUtc);
        }

        await using (var resolve = Scoped(options))
        {
            var pending = await resolve.NayaxMachineStockEvents.SingleAsync();
            pending.DuplicateResolution = NayaxDuplicateResolution.ReconciledManually;
            await resolve.SaveChangesAsync();
        }

        var resyncedAt = SyncStartedAt.AddMinutes(5);
        await using (var db = Scoped(options))
        {
            await Sync(db, AlertsReturning(), startedAtUtc: resyncedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);
        }

        Assert.Equal(resyncedAt, (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);
    }

    /// <summary>
    /// The pending check is the caller's own business's: another business's pending events on a
    /// machine that shares provider number 900 neither block this business's claim nor let it
    /// through.
    /// </summary>
    [Fact]
    public async Task Another_businesss_pending_events_do_not_block_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var other = TestAppDbContext.For(options, OtherBusinessId))
        {
            other.NayaxMachineStockEvents.Add(
                Event(11, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 3));
            await other.SaveChangesAsync();
        }

        await using (var db = Scoped(options))
        {
            await Sync(db, AlertsReturning(), startedAtUtc: SyncStartedAt, credentialRevision: 1)
                .Handle(Machine, CancellationToken.None);
        }

        Assert.Equal(SyncStartedAt, (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);
    }

    /// <summary>
    /// The pending check is a condition of the coverage <c>UPDATE</c> itself, not a read before
    /// it, so an event imported by a concurrent sync cannot land between the two.
    /// </summary>
    [Fact]
    public async Task A_pending_event_makes_the_conditional_write_refuse()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var seed = Scoped(options))
        {
            seed.NayaxMachineStockEvents.Add(
                Event(1, SyncStartedAt.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 3));
            await seed.SaveChangesAsync();
        }

        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.False(await store.TryRecordSuccessfulSyncAsync(
            LocalMachineId, SyncStartedAt, 1, NayaxMachineId, SyncStartedAt.AddDays(-14), default));
        Assert.True(await store.TryRecordSuccessfulSyncAsync(
            LocalMachineId, SyncStartedAt, 1, NayaxMachineId, SyncStartedAt, default));
    }

    #endregion

    #region The monotonic conditional write

    [Fact]
    public async Task A_later_sync_at_the_same_revision_advances_the_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await AtCredentialRevisionAsync(options, SellerBusinessId, 3);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, NayaxMachineId, AfterEveryEvent, default));
        Assert.True(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddMinutes(10), 3, NayaxMachineId, AfterEveryEvent, default));

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
        await AtCredentialRevisionAsync(options, SellerBusinessId, 3);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, NayaxMachineId, AfterEveryEvent, default));

        Assert.False(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddMinutes(-10), 3, NayaxMachineId, AfterEveryEvent, default));
        Assert.False(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, NayaxMachineId, AfterEveryEvent, default));

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
        await AtCredentialRevisionAsync(options, SellerBusinessId, 3);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 3, NayaxMachineId, AfterEveryEvent, default));

        // The Owner stores a new token, and a run under it succeeds.
        await AtCredentialRevisionAsync(options, SellerBusinessId, 4);
        Assert.True(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddMinutes(-30), 4, NayaxMachineId, AfterEveryEvent, default));

        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt.AddMinutes(-30), coverage.StartedAtUtc);
        Assert.Equal(4, coverage.CredentialRevision);
    }

    /// <summary>
    /// The credential-replacement case at the writer: a sync that began under revision 3 and
    /// finished after the Owner stored a new token must not mark revision 4 fresh.
    ///
    /// <para>With the connection on revision 4 this claim is refused twice over - the credential
    /// fence no longer recognises revision 3, and the stored coverage is already newer. Which is the
    /// point of keeping both: the fence cannot speak about a claim whose revision <em>is</em> still
    /// the stored one, and the ordering can, as
    /// <see cref="An_earlier_sync_finishing_last_does_not_move_the_coverage_backwards"/> shows.</para>
    /// </summary>
    [Fact]
    public async Task A_completion_under_a_superseded_revision_records_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await AtCredentialRevisionAsync(options, SellerBusinessId, 4);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 4, NayaxMachineId, AfterEveryEvent, default));

        Assert.False(
            await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt.AddHours(1), 3, NayaxMachineId, AfterEveryEvent, default));

        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt, coverage.StartedAtUtc);
        Assert.Equal(4, coverage.CredentialRevision);
    }

    /// <summary>
    /// The fence and the ordering are conditions <em>of</em> the write rather than steps before it:
    /// exactly one command reaches the database, it is the <c>UPDATE</c>, and both comparisons are
    /// inside its <c>WHERE</c> clause. A credential save committed while it runs cannot slip between
    /// the comparison and the write, because there is no between.
    /// </summary>
    [Fact]
    public async Task Both_comparisons_are_conditions_of_the_one_update_statement()
    {
        var commands = new List<string>();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection, commands);
        await using var db = Scoped(options);
        commands.Clear();

        Assert.True(
            await new EfMachineRestockSyncCoverageStore(db)
                .TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 1, NayaxMachineId, AfterEveryEvent, default));

        var statement = Assert.Single(commands);
        Assert.Contains("UPDATE \"Machines\"", statement, StringComparison.Ordinal);
        Assert.Contains("EXISTS", statement, StringComparison.Ordinal);
        Assert.Contains("BusinessNayaxConnections", statement, StringComparison.Ordinal);
        Assert.Contains("CredentialRevision", statement, StringComparison.Ordinal);
        Assert.Contains("LastSuccessfulRestockSyncStartedAtUtc", statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The credential fence on its own, with <strong>no coverage stored at all</strong>, which is
    /// the case the ordering comparison cannot refuse: a claim for a revision the connection no
    /// longer holds is turned away even though every value it offers is newer than nothing.
    ///
    /// <para>Then the honest claim lands, which is the whole property: only coverage bound to the
    /// credentials the business is actually on can become current.</para>
    /// </summary>
    [Fact]
    public async Task A_claim_for_a_revision_the_connection_no_longer_holds_is_refused_and_nothing_is_stored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await AtCredentialRevisionAsync(options, SellerBusinessId, 5);
        await using var db = Scoped(options);
        var store = new EfMachineRestockSyncCoverageStore(db);

        Assert.False(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 4, NayaxMachineId, AfterEveryEvent, default));

        var refused = await CoverageOfAsync(options, LocalMachineId);
        Assert.Null(refused.StartedAtUtc);
        Assert.Null(refused.CredentialRevision);
        Assert.False(MachineRestockSyncReminderPolicy.IsCoverageCurrent(refused.CredentialRevision, 5));

        Assert.True(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 5, NayaxMachineId, AfterEveryEvent, default));

        var recorded = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt, recorded.StartedAtUtc);
        Assert.True(MachineRestockSyncReminderPolicy.IsCoverageCurrent(recorded.CredentialRevision, 5));
    }

    /// <summary>
    /// The interleaving the reminder's provenance claim really rests on, end to end: the Owner
    /// stores a new token after this operation read the revision it would bind its claim to, but
    /// <strong>before</strong> the operation's own Nayax request resolved the credentials it
    /// authenticated with. So the request really ran under revision 5 while the use case's candidate
    /// was 4 - the mismatch the use case cannot see for itself, because it does not resolve the
    /// credentials its calls are made with.
    ///
    /// <para>Nothing is pre-seeded: the machine has no coverage at all, so the refusal cannot come
    /// from an already-newer stored pair. It comes from the fence inside the conditional write, and
    /// a misattributed claim for revision 4 - which the old, unfenced write would have accepted
    /// here - is what this test exists to refuse.</para>
    /// </summary>
    [Fact]
    public async Task A_credential_replaced_before_the_request_authenticated_records_no_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await AtCredentialRevisionAsync(options, SellerBusinessId, 4);

        int? revisionTheRequestUsed = null;

        await using (var db = Scoped(options))
        {
            using var connections = ConnectionStore(db);
            var requestCredentials = RequestCredentials(connections, SellerBusinessId);

            var nayax = AlertsReadDoing(async () =>
            {
                // Another request's save commits first, between this operation's revision read and
                // its own authentication.
                Assert.Equal(5, await SaveCredentialAsync(options, SellerBusinessId, "token-5"));

                revisionTheRequestUsed =
                    (await requestCredentials.GetForOperationAsync(CancellationToken.None)).CredentialRevision;
            });

            await SyncWith(db, nayax, startedAtUtc: SyncStartedAt, connections: connections)
                .Handle(Machine, CancellationToken.None);
        }

        // The request authenticated under the replaced credentials, not the ones the operation read.
        Assert.Equal(5, revisionTheRequestUsed);

        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Null(coverage.StartedAtUtc);
        Assert.Null(coverage.CredentialRevision);
    }

    /// <summary>
    /// The other side of the same window: the operation's request authenticated under revision 4 -
    /// so its claim's provenance is sound - and the Owner stored a new token afterwards, before the
    /// operation reached its coverage write. Again with nothing pre-seeded.
    ///
    /// <para>That claim is refused too, and losing it costs nothing: the reminder reads coverage as
    /// current only at an exact revision match (<see cref="MachineRestockSyncReminderPolicy"/>), so
    /// revision 4's coverage would be read as no recorded sync the moment it was written. Refusing
    /// it keeps the row from holding a claim about superseded credentials at all. The sync that then
    /// runs under revision 5 records normally, which is what makes this a fence and not a
    /// lock-out.</para>
    /// </summary>
    [Fact]
    public async Task A_credential_saved_after_the_request_authenticated_records_no_coverage_until_a_bound_sync_runs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await AtCredentialRevisionAsync(options, SellerBusinessId, 4);

        int? revisionTheRequestUsed = null;

        await using (var db = Scoped(options))
        {
            using var connections = ConnectionStore(db);
            var requestCredentials = RequestCredentials(connections, SellerBusinessId);

            var nayax = AlertsReadDoing(async () =>
            {
                revisionTheRequestUsed =
                    (await requestCredentials.GetForOperationAsync(CancellationToken.None)).CredentialRevision;

                Assert.Equal(5, await SaveCredentialAsync(options, SellerBusinessId, "token-5"));
            });

            await SyncWith(db, nayax, startedAtUtc: SyncStartedAt, connections: connections)
                .Handle(Machine, CancellationToken.None);
        }

        Assert.Equal(4, revisionTheRequestUsed);

        var refused = await CoverageOfAsync(options, LocalMachineId);
        Assert.Null(refused.StartedAtUtc);
        Assert.Null(refused.CredentialRevision);

        // And the next sync, bound to the credentials the business is now on, is recorded.
        await using (var db = Scoped(options))
        {
            using var connections = ConnectionStore(db);

            await SyncWith(db, AlertsReturning(), startedAtUtc: SyncStartedAt.AddHours(1), connections: connections)
                .Handle(Machine, CancellationToken.None);
        }

        var recorded = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt.AddHours(1), recorded.StartedAtUtc);
        Assert.True(MachineRestockSyncReminderPolicy.IsCoverageCurrent(recorded.CredentialRevision, 5));
    }

    /// <summary>
    /// Earlier and later completions of operations that are genuinely <em>in flight</em> at the same
    /// time, over the relational writer, with barriers deciding the order rather than a race.
    ///
    /// <para>Three syncs overlap: this business's 08:00 run and the other business's 08:20 run are
    /// both parked inside their own Nayax request while this business's 08:10 run completes and
    /// commits; then the other business's finishes, then the 08:00 one. The 08:00 run must not move
    /// its own business's coverage back from 08:10 when it finally lands, and neither in-flight
    /// operation may touch the other business's machine - the two machines share provider number
    /// 900.</para>
    /// </summary>
    [Fact]
    public async Task In_flight_syncs_keep_the_later_start_and_stay_inside_their_own_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        var earlyParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEarly = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var early = Scoped(options);
        await using var later = Scoped(options);
        await using var other = TestAppDbContext.For(options, OtherBusinessId);
        using var earlyConnections = ConnectionStore(early);
        using var laterConnections = ConnectionStore(later);
        using var otherConnections = ConnectionStore(other);

        // In flight, parked inside its alert read: it has read its revision and done nothing else.
        var earlySync = SyncWith(
                early,
                AlertsReadDoing(async () =>
                {
                    earlyParked.SetResult();
                    await releaseEarly.Task;
                }),
                startedAtUtc: SyncStartedAt,
                connections: earlyConnections)
            .Handle(Machine, CancellationToken.None);

        await earlyParked.Task;

        var otherSync = SyncWith(
                other,
                AlertsReadDoing(async () =>
                {
                    otherParked.SetResult();
                    await releaseOther.Task;
                }),
                startedAtUtc: SyncStartedAt.AddMinutes(20),
                connections: otherConnections)
            .Handle(OtherMachine, CancellationToken.None);

        await otherParked.Task;

        // The later-started run of this business completes and commits while both are parked.
        await SyncWith(later, AlertsReturning(), startedAtUtc: SyncStartedAt.AddMinutes(10), connections: laterConnections)
            .Handle(Machine, CancellationToken.None);

        Assert.Equal(SyncStartedAt.AddMinutes(10), (await CoverageOfAsync(options, LocalMachineId)).StartedAtUtc);

        releaseOther.SetResult();
        await otherSync;

        releaseEarly.SetResult();
        await earlySync;

        // The run that started first finished last and claimed nothing: it can only have accounted
        // for a subset of what the 08:10 run saw.
        var coverage = await CoverageOfAsync(options, LocalMachineId);
        Assert.Equal(SyncStartedAt.AddMinutes(10), coverage.StartedAtUtc);
        Assert.Equal(1, coverage.CredentialRevision);

        // And the other business's own in-flight run recorded its own machine, nothing else.
        var otherCoverage = await CoverageOfAsync(options, OtherLocalMachineId);
        Assert.Equal(SyncStartedAt.AddMinutes(20), otherCoverage.StartedAtUtc);
        Assert.Equal(1, otherCoverage.CredentialRevision);
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
            // Revision 1 is this caller's own live revision, so the credential fence passes and the
            // refusal is the central tenant filter - which is the thing under test here.
            Assert.False(
                await new EfMachineRestockSyncCoverageStore(db)
                    .TryRecordSuccessfulSyncAsync(OtherLocalMachineId, SyncStartedAt, 1, NayaxMachineId, AfterEveryEvent, default));
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
            .GetProviderMachineCoverageAsync(AllTime, CancellationToken.None);

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

    /// <summary>
    /// The pending window and the quantity rule (issue #948), relationally: an event at exactly the
    /// window's start counts and one a second before does not, a zero or negative quantity never
    /// counts, an unparsed event with no quantity still does, and the oldest pending instant is the
    /// oldest <em>counted</em> event. Nothing outside the bounds is changed.
    /// </summary>
    [Fact]
    public async Task The_aggregate_read_counts_only_positive_or_unparsed_events_inside_the_pending_window()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var windowStart = EventTime.AddDays(-14);

        await using (var seed = Scoped(options))
        {
            seed.NayaxMachineStockEvents.AddRange(
                Event(1, windowStart, NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 6),
                Event(2, windowStart.AddSeconds(-1), NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 6),
                Event(3, EventTime.AddDays(-60), NayaxStockEventProcessingStatus.Unprocessed),
                Event(4, EventTime, NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: -2),
                Event(5, EventTime, NayaxStockEventProcessingStatus.Unprocessed, parsedQuantity: 0),
                Event(6, EventTime.AddDays(-1), NayaxStockEventProcessingStatus.Unprocessed),
                Event(7, EventTime, NayaxStockEventProcessingStatus.Applied, parsedQuantity: 4));
            await seed.SaveChangesAsync();
        }

        await using (var db = Scoped(options))
        {
            var machine = Assert.Single(
                await new EfMachineRestockSyncCoverageStore(db)
                    .GetProviderMachineCoverageAsync(windowStart, CancellationToken.None));

            // Event 1 (on the boundary, positive) and event 6 (unparsed, needs review).
            Assert.Equal(2, machine.KnownPendingEventCount);
            Assert.Equal(windowStart, machine.OldestKnownPendingEventAtUtc);
        }

        await using var verify = Scoped(options);
        Assert.Equal(
            6,
            await verify.NayaxMachineStockEvents.CountAsync(
                stockEvent => stockEvent.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed));
    }

    [Fact]
    public async Task A_provider_named_machine_with_nothing_outstanding_is_reported_with_no_pending_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using var db = Scoped(options);
        var machine = Assert.Single(
            await new EfMachineRestockSyncCoverageStore(db).GetProviderMachineCoverageAsync(AllTime, CancellationToken.None));

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
        await AtCredentialRevisionAsync(options, OtherBusinessId, 9);

        await using (var other = TestAppDbContext.For(options, OtherBusinessId))
        {
            other.NayaxMachineStockEvents.AddRange(
                Event(11, EventTime, NayaxStockEventProcessingStatus.Unprocessed),
                Event(12, EventTime, NayaxStockEventProcessingStatus.Unprocessed));
            await other.SaveChangesAsync();

            Assert.True(
                await new EfMachineRestockSyncCoverageStore(other)
                    .TryRecordSuccessfulSyncAsync(OtherLocalMachineId, SyncStartedAt, 9, NayaxMachineId, AfterEveryEvent, default));
        }

        await using var db = Scoped(options);
        var machine = Assert.Single(
            await new EfMachineRestockSyncCoverageStore(db).GetProviderMachineCoverageAsync(AllTime, CancellationToken.None));

        Assert.Equal(LocalMachineId, machine.LocalMachineId);
        Assert.Equal(0, machine.KnownPendingEventCount);
        Assert.Null(machine.LastSuccessfulSyncStartedAtUtc);

        // And the other business still sees its own.
        await using var otherRead = TestAppDbContext.For(options, OtherBusinessId);
        var otherMachine = Assert.Single(
            await new EfMachineRestockSyncCoverageStore(otherRead)
                .GetProviderMachineCoverageAsync(AllTime, CancellationToken.None));

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

        Assert.Empty(await store.GetProviderMachineCoverageAsync(AllTime, CancellationToken.None));
        Assert.False(await store.TryRecordSuccessfulSyncAsync(LocalMachineId, SyncStartedAt, 1, NayaxMachineId, AfterEveryEvent, default));
    }

    #endregion

    #region Fixtures

    private static Task<DbContextOptions<AppDbContext>> CreateDatabaseAsync(SqliteConnection connection) =>
        CreateDatabaseAsync(connection, commands: null);

    private static async Task<DbContextOptions<AppDbContext>> CreateDatabaseAsync(
        SqliteConnection connection, List<string>? commands)
    {
        await connection.OpenAsync();
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);

        if (commands is not null)
        {
            builder.AddInterceptors(new CommandTextInterceptor(commands));
        }

        var options = builder.Options;

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

        // Both businesses have really stored Nayax credentials, written by the real relational
        // writer, so each starts at credential revision 1. The coverage write under test is fenced
        // on the *stored* revision (issue #891), so a test that offers one has to say which save
        // made it current - which is what AtCredentialRevisionAsync below does.
        await SaveCredentialAsync(options, SellerBusinessId, "token-a-1");
        await SaveCredentialAsync(options, OtherBusinessId, "token-b-1");

        return options;
    }

    private static AppDbContext Scoped(DbContextOptions<AppDbContext> options) =>
        TestAppDbContext.For(options, SellerBusinessId);

    /// <summary>
    /// Stores new credentials for a business through the real
    /// <see cref="EfNayaxConnectionStore"/> - the writer that increments the credential revision in
    /// the database - and answers the revision that landed. This is what an Owner pressing Save in
    /// another request does, and it is deliberately not a hand-written row: the revision a test
    /// offers must be one a real save produced.
    /// </summary>
    private static async Task<int> SaveCredentialAsync(
        DbContextOptions<AppDbContext> options, int businessId, string accessToken)
    {
        await using var db = TestAppDbContext.For(options, businessId);
        using var store = ConnectionStore(db);
        var saved = await store.SaveCredentialAsync("operator-id", accessToken, CancellationToken.None);
        return saved.CredentialRevision;
    }

    /// <summary>
    /// Replaces the business's credentials until the stored revision is
    /// <paramref name="credentialRevision"/>, one real save per step.
    /// </summary>
    private static async Task AtCredentialRevisionAsync(
        DbContextOptions<AppDbContext> options, int businessId, int credentialRevision)
    {
        await using var db = TestAppDbContext.For(options, businessId);
        using var store = ConnectionStore(db);

        for (var revision = (await store.FindAsync(CancellationToken.None))!.CredentialRevision;
            revision < credentialRevision;
            revision++)
        {
            await store.SaveCredentialAsync("operator-id", $"token-{revision + 1}", CancellationToken.None);
        }

        Assert.Equal(
            credentialRevision, (await store.FindAsync(CancellationToken.None))!.CredentialRevision);
    }

    /// <summary>
    /// The real connection store over one request's context, with real AES-GCM encryption, so the
    /// revision the sync reads and the revision a concurrent save writes are the same stored column.
    /// The key material is this test's own throwaway value, like every other connection test's.
    /// </summary>
    private static EfNayaxConnectionStore ConnectionStore(AppDbContext db)
    {
        var protection = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        protection.Keys[ActiveKeyId] = Convert.ToBase64String(
            Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

        return new EfNayaxConnectionStore(
            db,
            new AesGcmNayaxTokenProtector(protection),
            new FakeClock(SyncStartedAt),
            NullLogger<EfNayaxConnectionStore>.Instance);
    }

    /// <summary>
    /// The credentials one ordinary Nayax request of this operation would authenticate with,
    /// resolved by the real provider over the real store - the snapshot the use case itself never
    /// sees.
    /// </summary>
    private static INayaxRequestCredentialProvider RequestCredentials(
        INayaxConnectionStore connections, int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));

        return new NayaxRequestCredentialProvider(
            connections, scope, TestCurrentPlan.Unrestricted(), new FakeClock(SyncStartedAt));
    }

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
        SyncWith(
            db,
            nayax,
            startedAtUtc,
            new FakeNayaxConnectionStore("op", "token", NayaxConnectionStatus.Ready, credentialRevision),
            coverage);

    /// <summary>
    /// The use case over a caller-supplied connection store, for the tests whose subject is the
    /// stored connection itself: the real <see cref="EfNayaxConnectionStore"/>, so the revision the
    /// sync reads before its first Nayax call is a real read of the row a concurrent save replaces.
    /// </summary>
    private static SyncMachineStockFromNayax SyncWith(
        AppDbContext db,
        Mock<INayaxLynxClient> nayax,
        DateTime startedAtUtc,
        INayaxConnectionStore connections,
        IMachineRestockSyncCoverageStore? coverage = null) =>
        new(
            nayax.Object,
            new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfNayaxProductLinkStore(db),
            new FakeClock(startedAtUtc),
            connections,
            coverage ?? new EfMachineRestockSyncCoverageStore(db),
            new FakeBusinessCalendar(startedAtUtc));

    /// <summary>
    /// A successful - and deliberately empty - alert read that runs <paramref name="atTheRequest"/>
    /// at the moment the operation's own Nayax request happens, which is the only place a test can
    /// interleave anything between the use case's revision read and its coverage write.
    /// </summary>
    private static Mock<INayaxLynxClient> AlertsReadDoing(Func<Task> atTheRequest)
    {
        var mock = new Mock<INayaxLynxClient>();
        mock.Setup(client => client.GetMachineLastAlertsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() => NoAlertsAfterAsync(atTheRequest));
        return mock;
    }

    private static async Task<List<NayaxMachineAlert>> NoAlertsAfterAsync(Func<Task> atTheRequest)
    {
        await atTheRequest();
        return [];
    }

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
        NayaxDuplicateResolution duplicateResolution = NayaxDuplicateResolution.None,
        int? parsedQuantity = null) => new()
        {
            NayaxEventLogId = eventLogId,
            MachineId = NayaxMachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = eventDateTimeGmt,
            EventDateTimeVmc = DateTime.SpecifyKind(eventDateTimeGmt.AddHours(10), DateTimeKind.Unspecified),
            RawEventData = "Adjusted Stock",
            ParsedQuantity = parsedQuantity,
            MatchStatus = NayaxStockEventMatchStatus.NeedsReview,
            ProcessingStatus = processingStatus,
            DuplicateResolution = duplicateResolution,
        };

    /// <summary>Records the text of every command the context executes, in order.</summary>
    private sealed class CommandTextInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands;

        public CommandTextInterceptor(List<string> commands) => _commands = commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            _commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

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
