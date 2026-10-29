using System.Net;
using Inventory.Application.ConnectedMachines;
using Inventory.Application.Nayax;
using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The connected-machine snapshot's account attribution across a Nayax account change, end to end
/// and deterministically (issue #978).
///
/// <para><strong>Nothing here is mocked except the HTTP handler at the very edge.</strong> The
/// credential read, the status gate, the AES-GCM decryption, the account-series <c>UPDATE</c>, the
/// unique daily claim and the tenant filters are all the production ones over relational SQLite with
/// the real migration history; the one double is a handler that <em>pauses</em> the fleet response,
/// lets the test save a different Nayax account from a second simulated request, and only then
/// answers. That is the interleaving the defect this file pins lived in, and it is deterministic
/// rather than timed: the test waits for the request to be in flight, and the request waits for the
/// test to release it.</para>
///
/// <para><strong>What has to hold, on both paths.</strong> A fleet read is evidence about the account
/// whose credentials authenticated it, so a snapshot is stamped with the series carried by that very
/// credential snapshot. The refresh path reads the fleet and <em>then</em> records, so the defect
/// stamped the old account's machines with the new account's series; the daily job reads eligibility
/// and <em>then</em> the fleet, so it did the reverse. Both now carry the series out of the request,
/// which gives three things at once: the fleet lands under the account it came from, the other
/// account's day is never claimed by it, and a later valid read of that other account still records
/// its own snapshot the same day.</para>
///
/// <para>A <em>token rotation</em> on the same account is the control case: a new credential revision
/// but the same Nayax account, which must not start a new series or split a fleet's evidence.</para>
/// </summary>
public class ConnectedMachineSnapshotAccountSwitchTests : IAsyncLifetime
{
    private const string ActiveKeyId = "test-key";
    private const string OperatorA = "4242";
    private const string OperatorB = "9999";
    private const string TokenA = "fake-token-a-not-a-real-credential";
    private const string TokenB = "fake-token-b-not-a-real-credential";

    private static readonly DateTime Now = new(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _options = null!;
    private int _businessId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var setup = TestAppDbContext.Unrestricted(_options);
        await setup.Database.MigrateAsync();

        var business = new Business
        {
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = Now.AddYears(-1),
        };
        setup.Businesses.Add(business);
        await setup.SaveChangesAsync();
        _businessId = business.Id;

        await ConnectAsync(OperatorA, TokenA);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>
    /// <strong>The refresh path.</strong> A dashboard refresh resolves account A's credentials and
    /// its fleet request leaves the API; while that request is in flight an Owner connects account B;
    /// only then does A's fleet answer arrive and become the day's snapshot.
    ///
    /// <para>The row must name series 1 - A's - because those are the credentials the request was
    /// sent with, and the machines it holds are A's machines. The previous behaviour read the
    /// connection again at recording time and stamped them with series 2.</para>
    /// </summary>
    [Fact]
    public async Task A_fleet_read_that_finishes_after_the_account_changed_is_recorded_under_the_account_it_used()
    {
        await using var request = NewRequest();
        var handler = new PausingFleetHandler(101, 102, 103);
        var client = request.CountingClientFor(handler);

        var reading = client.GetMachinesFreshAsync(CancellationToken.None);

        // The request is authenticated and in flight. Now a separate Owner request connects a
        // different Nayax account, which moves the stored series to 2.
        await handler.InFlight;
        await ConnectAsync(OperatorB, TokenB);
        Assert.Equal(2, await StoredSeriesAsync());

        handler.Release();
        var machines = await reading;

        Assert.Equal(3, machines.Count);
        Assert.Equal($"Bearer {TokenA}", Assert.Single(handler.Authorizations));

        var row = Assert.Single(await StoredSnapshotsAsync());
        Assert.Equal(1, row.AccountSeries);
        Assert.Equal(Today, row.SnapshotDateUtc);
        Assert.Equal(3, row.MachineCount);
        Assert.Equal("101,102,103", row.ProviderMachineIds);
    }

    /// <summary>
    /// And the other half of the guarantee: because the superseded answer claimed only its own
    /// series, a valid read of the <em>new</em> account records its own snapshot the same day. One day
    /// then holds one row per series, which is #978's account-change rule, and nothing ever edits the
    /// first row.
    /// </summary>
    [Fact]
    public async Task The_new_account_records_its_own_snapshot_the_same_day()
    {
        await using var superseded = NewRequest();
        var firstHandler = new PausingFleetHandler(101, 102, 103);
        var reading = superseded.CountingClientFor(firstHandler).GetMachinesFreshAsync(CancellationToken.None);

        await firstHandler.InFlight;
        await ConnectAsync(OperatorB, TokenB);
        firstHandler.Release();
        await reading;

        // The new account's own read, on its own request, the same UTC day.
        await using var current = NewRequest();
        var secondHandler = new PausingFleetHandler(201);
        secondHandler.Release();
        var machines = await current.CountingClientFor(secondHandler).GetMachinesFreshAsync(CancellationToken.None);

        Assert.Equal(201, Assert.Single(machines).MachineID);
        Assert.Equal($"Bearer {TokenB}", Assert.Single(secondHandler.Authorizations));

        var rows = await StoredSnapshotsAsync();

        Assert.Equal(
            [(1, 3, "101,102,103"), (2, 1, "201")],
            rows.Select(row => (row.AccountSeries, row.MachineCount, row.ProviderMachineIds)).ToArray());
        Assert.All(rows, row => Assert.Equal(Today, row.SnapshotDateUtc));

        // And the day's count is the later series' row, which is the one Domain rule for it.
        var latest = ConnectedMachineSnapshotPolicy.Latest(
            [.. rows.Select(row => new ConnectedMachineSnapshot(
                row.SnapshotDateUtc,
                row.AccountSeries,
                row.MachineCount,
                ConnectedMachineFleetRead.ParseIds(row.ProviderMachineIds),
                row.TakenAtUtc,
                row.ReadWasComplete))]);

        Assert.Equal(2, latest!.AccountSeries);
        Assert.Equal(1, latest.MachineCount);
    }

    /// <summary>
    /// <strong>The daily job's path - the mirror image.</strong> The job decides a business is worth a
    /// call while it is still on account A, and the Owner connects account B before the fleet request
    /// is made. The request therefore authenticates as B, so B is what the snapshot names, and A's day
    /// is left unclaimed rather than being given B's fleet.
    /// </summary>
    [Fact]
    public async Task The_jobs_read_is_recorded_under_the_account_the_request_authenticated_as()
    {
        await using var request = NewRequest();
        var handler = new PausingFleetHandler(201, 202);
        handler.Release();

        // The job's eligibility read happens first, on account A. The switch lands before the fleet
        // request resolves its credentials, which is the window this path's defect lived in.
        var recorder = request.Recorder(
            handler,
            beforeFleetRead: () => ConnectAsync(OperatorB, TokenB));

        var attempt = await recorder.ReadFleetAndRecordAsync(CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.Recorded, attempt.Outcome);
        Assert.True(attempt.FleetWasRead);
        Assert.Equal($"Bearer {TokenB}", Assert.Single(handler.Authorizations));

        var row = Assert.Single(await StoredSnapshotsAsync());
        Assert.Equal(2, row.AccountSeries);
        Assert.Equal("201,202", row.ProviderMachineIds);
    }

    /// <summary>
    /// The control case: rotating the token of the <em>same</em> Nayax account is a new credential
    /// revision and the same account, so the series does not move, the day stays claimed by the
    /// snapshot already recorded, and the second read records nothing. A fleet's evidence is not split
    /// because an operator pasted a fresh token.
    /// </summary>
    [Fact]
    public async Task A_token_rotation_on_the_same_account_keeps_the_series_and_the_days_claim()
    {
        await using var first = NewRequest();
        var firstHandler = new PausingFleetHandler(101, 102);
        firstHandler.Release();
        await first.CountingClientFor(firstHandler).GetMachinesFreshAsync(CancellationToken.None);

        // The same operator id with a different token.
        await ConnectAsync(OperatorA, "fake-token-a-rotated-not-a-real-credential");
        Assert.Equal(1, await StoredSeriesAsync());
        Assert.Equal(2, await StoredRevisionAsync());

        await using var second = NewRequest();
        var secondHandler = new PausingFleetHandler(101, 102, 103);
        secondHandler.Release();
        await second.CountingClientFor(secondHandler).GetMachinesFreshAsync(CancellationToken.None);

        // Still one row, still series 1, and still the count the first read recorded: a snapshot is
        // append-only evidence and the day was already claimed for this account.
        var row = Assert.Single(await StoredSnapshotsAsync());
        Assert.Equal(1, row.AccountSeries);
        Assert.Equal(2, row.MachineCount);
        Assert.Equal("101,102", row.ProviderMachineIds);
    }

    /// <summary>Stores credentials and leaves the connection usable, as a separate Owner request does.</summary>
    private async Task ConnectAsync(string operatorId, string accessToken)
    {
        await using var db = TestAppDbContext.For(_options, _businessId);
        using var store = StoreFor(db);

        var saved = await store.SaveCredentialAsync(operatorId, accessToken, CancellationToken.None);

        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(
                saved.CredentialRevision, NayaxConnectionStatus.Ready, Now),
            CancellationToken.None));
    }

    private async Task<int> StoredSeriesAsync() => (await StoredConnectionAsync()).AccountSeries;

    private async Task<int> StoredRevisionAsync() => (await StoredConnectionAsync()).CredentialRevision;

    private async Task<BusinessNayaxConnection> StoredConnectionAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.BusinessNayaxConnections.AsNoTracking().SingleAsync();
    }

    private async Task<List<BusinessConnectedMachineSnapshot>> StoredSnapshotsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.BusinessConnectedMachineSnapshots
            .AsNoTracking()
            .Where(row => row.BusinessId == _businessId)
            .OrderBy(row => row.AccountSeries)
            .ToListAsync();
    }

    private SimulatedRequest NewRequest() => new(_options, _businessId);

    private static EfNayaxConnectionStore StoreFor(AppDbContext db)
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys[ActiveKeyId] = Convert.ToBase64String(
            Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

        return new EfNayaxConnectionStore(
            db, new AesGcmNayaxTokenProtector(options), new FakeClock(Now), new CapturingLogger<EfNayaxConnectionStore>());
    }

    /// <summary>
    /// One request's scope, composed as the container composes it: one <see cref="AppDbContext"/>
    /// behind the connection store, the credential provider, the Lynx client with its machine-list
    /// cache, the snapshot store and the one writer.
    /// </summary>
    private sealed class SimulatedRequest : IAsyncDisposable
    {
        private readonly AppDbContext _db;
        private readonly EfNayaxConnectionStore _store;
        private readonly INayaxRequestCredentialProvider _credentials;
        private readonly int _businessId;

        public SimulatedRequest(DbContextOptions<AppDbContext> options, int businessId)
        {
            _businessId = businessId;
            _db = TestAppDbContext.For(options, businessId);
            _store = StoreFor(_db);
            _credentials = new NayaxRequestCredentialProvider(
                _store,
                TestAppDbContext.ScopeFor(businessId),
                TestCurrentPlan.Unrestricted(),
                new FakeClock(Now));
        }

        /// <summary>
        /// The refresh path exactly as it is registered: the connected-machine counter outside the
        /// machine-list cache outside the HTTP client.
        /// </summary>
        public INayaxLynxClient CountingClientFor(HttpMessageHandler handler) =>
            new ConnectedMachineCountingNayaxLynxClient(
                CachedClientFor(handler),
                Recorder(fleet: null),
                new CapturingLogger<ConnectedMachineCountingNayaxLynxClient>());

        /// <summary>
        /// The one writer, with the daily job's own fleet reader over the inner typed client - and an
        /// optional hook that runs after the job has decided to spend the call and before the fleet
        /// request resolves its credentials.
        /// </summary>
        public IConnectedMachineSnapshotRecorder Recorder(
            HttpMessageHandler handler, Func<Task> beforeFleetRead) =>
            Recorder(new HookedFleetReader(
                new NayaxConnectedMachineFleetReader(LynxClientFor(handler)), beforeFleetRead));

        public async ValueTask DisposeAsync()
        {
            _store.Dispose();
            await _db.DisposeAsync();
        }

        private IConnectedMachineSnapshotRecorder Recorder(IConnectedMachineFleetReader? fleet) =>
            new RecordConnectedMachineSnapshot(
                _store,
                new EfConnectedMachineSnapshotStore(_db),
                new FakeManualPlanGrantStore(new ManualPlanGrantState(
                    SubscriptionPlan.Pro,
                    ManualPlanGrantSource.Grandfathered,
                    new EntitlementPeriod(Now.AddYears(-1), null))),
                new FakeBillingSubscriptionStore(),
                fleet ?? new UnreachableFleetReader(),
                new FakeClock(Now));

        private CachingNayaxLynxClient CachedClientFor(HttpMessageHandler handler) =>
            new(
                LynxClientFor(handler),
                _credentials,
                TestAppDbContext.ScopeFor(_businessId),
                new NayaxMachineListCache(new FakeClock(Now)),
                new NayaxMachineProductsCache(new FakeClock(Now)));

        private NayaxLynxClient LynxClientFor(HttpMessageHandler handler) =>
            new(
                // A deliberately unroutable host: this test must never reach a real Nayax endpoint.
                new HttpClient(handler) { BaseAddress = new Uri("https://nayax.invalid/operational/v1/") },
                _credentials,
                new CapturingLogger<NayaxLynxClient>());
    }

    /// <summary>
    /// A fleet reader that runs <paramref name="beforeRead"/> and then reads, so a test can land an
    /// account change in the window between the job deciding to spend a call and the call resolving
    /// its credentials. It is deliberately a decorator over the real reader: what is under test is
    /// the real credential resolution, not a stand-in for it.
    /// </summary>
    private sealed class HookedFleetReader(IConnectedMachineFleetReader inner, Func<Task> beforeRead)
        : IConnectedMachineFleetReader
    {
        public async Task<ConnectedMachineFleetRead> ReadAsync(CancellationToken cancellationToken)
        {
            await beforeRead().ConfigureAwait(false);

            return await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The fleet reader of a refresh-path recorder, which never reads a fleet of its own.</summary>
    private sealed class UnreachableFleetReader : IConnectedMachineFleetReader
    {
        public Task<ConnectedMachineFleetRead> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The refresh path records from a read a feature already performed and never reads "
                    + "the fleet itself.");
    }

    /// <summary>
    /// Answers the Lynx fleet endpoint with a fixed fleet, and holds the response until the test
    /// releases it - recording the <c>Authorization</c> header each request carried, which is how the
    /// test says <em>which account</em> a request was really sent with.
    /// </summary>
    private sealed class PausingFleetHandler : HttpMessageHandler
    {
        private readonly long[] _machineIds;
        private readonly TaskCompletionSource _inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PausingFleetHandler(params long[] machineIds) => _machineIds = machineIds;

        /// <summary>Completes once a request has been authenticated and sent.</summary>
        public Task InFlight => _inFlight.Task;

        /// <summary>The bearer header of every request, in order. Never the stored ciphertext.</summary>
        public List<string?> Authorizations { get; } = [];

        /// <summary>Lets the pending response - or the next one - complete.</summary>
        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Authorizations)
            {
                Authorizations.Add(request.Headers.Authorization?.ToString());
            }

            _inFlight.TrySetResult();

            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var machines = string.Join(
                ",",
                _machineIds.Select(id => $"{{\"MachineID\":{id}}}"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"[{machines}]", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
