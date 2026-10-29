using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Sites;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The parallel Nayax paths over one request's scoped persistence (issue #667): the real
/// <see cref="NayaxLynxClient"/> over the real <see cref="NayaxRequestCredentialProvider"/> over the
/// real <see cref="EfNayaxConnectionStore"/> and real AES-GCM encryption, on relational SQLite, with
/// the real fan-out use cases and two businesses.
///
/// Since issue #520 every ordinary Nayax call resolves the business's own credentials through the
/// request's one scoped <see cref="AppDbContext"/>, and a 401 writes a status back through it. A
/// bounded fan-out therefore puts several credential reads - and several status writes - on a context
/// that supports exactly one operation at a time, which is the defect these tests pin. Nothing here
/// mocks the Nayax client: the only test double is the HTTP handler at the very edge, so the
/// credential read, the gate, the decryption, the conditional status write and the tenant filters are
/// all the production ones.
///
/// <para><strong>Why the interceptor.</strong> SQLite's async implementation is synchronous, so a
/// second operation normally starts only after the first has finished and the overlap never happens
/// in a test while a genuinely asynchronous provider rejects it (docs/architecture.md § Concurrency
/// inside one request). <see cref="ContextCommandRecorder"/> makes the request's context genuinely
/// asynchronous by parking every command briefly inside EF's own critical section, which is what
/// turns the defect into a deterministic failure here rather than a source-level argument: before the
/// fix these tests fail with EF's "a second operation was started on this context instance", and
/// after it the recorder reports that one command was ever in flight.</para>
/// </summary>
public class NayaxParallelCredentialAccessTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorA = "2002736764";
    private const string OperatorB = "9009009009";
    private const string TokenA = "fake-token-for-business-a";
    private const string TokenB = "fake-token-for-business-b";
    private const string ReplacementTokenA = "fake-replacement-token-for-business-a";
    private const string ActiveKeyId = "2026-10";
    private const long ProductId = 100;

    /// <summary>
    /// The local product the Nayax product id above is linked to (issue #733). Deliberately not the
    /// same number: a resolved need attributed to it could not have come from reading the remote id
    /// as a local one.
    /// </summary>
    private const long LocalProductId = 4100;

    private const long SiteId = 7;

    /// <summary>Four machines, so the bounded fan-out really does fan out.</summary>
    private static readonly long[] MachineIds = [10, 11, 12, 13];

    private static readonly DateTime Now = new(2026, 10, 15, 1, 2, 3, DateTimeKind.Utc);
    private static readonly DateTime TestedAt = new(2026, 10, 15, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>
    /// The reorder fan-out: one fleet read, four concurrent machine-product reads and then one
    /// outstanding-order read, all on the one request context. The remote requests must still
    /// overlap - that is the whole point of the bounded fan-out - while the context must never see
    /// two operations at once, and every call must resolve its own credentials rather than reusing a
    /// snapshot the request took earlier.
    /// </summary>
    [Fact]
    public async Task A_bounded_machine_fan_out_never_puts_two_operations_on_the_request_context()
    {
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        // The local product the machines' Nayax product id names, through the real recorded id: a
        // local id deliberately different from the remote one, so the resolved need is attributed to
        // the product that carries the remote id (issue #733).
        await fixture.AddProductAsync(fixture.BusinessA, LocalProductId, "Chips");
        await fixture.RecordNayaxProductIdAsync(fixture.BusinessA, ProductId, LocalProductId);
        await using var request = fixture.Request(fixture.BusinessA);
        var handler = new FanOutHandler(Respond.Catalogue, concurrentMachineProductRequests: MachineIds.Length);
        var useCase = new CalculateReorderNeeds(
            request.ClientFor(handler),
            new EfOutstandingSupplierOrderQuantityStore(request.Db),
            new EfNayaxProductLinkStore(request.Db),
            MachineIds.Length);

        var result = await useCase.Handle(CancellationToken.None);

        Assert.Equal(MachineIds, result.MachineIds);
        Assert.Equal(
            MachineIds.Length * 2,
            result.MachineReplenishmentNeedByProductId.GetValueOrDefault(LocalProductId));
        Assert.False(result.MachineReplenishmentNeedByProductId.ContainsKey(ProductId));
        Assert.Equal(1, request.Commands.MaxConcurrentCommands);
        Assert.Equal(MachineIds.Length, handler.MaxConcurrentRequests);

        // One connection read per Nayax call - the fleet read plus one per machine - and not one
        // fewer: a request that cached a usable credential would show fewer reads, and the status
        // gate would stop closing between calls.
        Assert.Equal(MachineIds.Length + 1, request.Commands.ConnectionCommandCount);
    }

    /// <summary>
    /// Every 401 of one fan-out arrives while the others are still in flight, so the conditional
    /// status writes overlap as well as the reads. All of them describe the credentials that were
    /// actually sent, so each is applied and each caller gets the same stable "not connected"
    /// answer, leaving one <see cref="NayaxConnectionStatus.NeedsAttention"/> row at the revision
    /// the calls used.
    /// </summary>
    [Fact]
    public async Task Concurrent_401s_all_report_not_connected_and_write_one_needs_attention_status()
    {
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await using var request = fixture.Request(fixture.BusinessA);
        var handler = new FanOutHandler(
            Respond.Unauthorized, concurrentMachineProductRequests: MachineIds.Length);
        var client = request.ClientFor(handler);

        var failures = await Task.WhenAll(MachineIds
            .Select(machineId => Record.ExceptionAsync(
                () => client.GetMachineProductsAsync(machineId, CancellationToken.None)))
            .ToList());

        Assert.All(failures, failure => Assert.Equal(
            NayaxConnectionStatus.NeedsAttention,
            Assert.IsType<NayaxNotConnectedException>(failure).Status));
        Assert.Equal(1, request.Commands.MaxConcurrentCommands);
        Assert.Equal(MachineIds.Length, handler.MaxConcurrentRequests);

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, stored.Status);
        Assert.Equal(1, stored.CredentialRevision);
        Assert.Equal(Now, stored.LastTestedAtUtc);
    }

    /// <summary>
    /// The interleaving the credential revision exists for, now with a whole fan-out inside it: all
    /// four calls resolve revision 1 and are in flight when an Owner's separate request stores a
    /// replacement token, and only then do the 401s arrive. Every one of them answers for
    /// credentials that no longer exist, so every one is discarded - a fan-out of stale results must
    /// not mark a token an operator has just fixed as broken, however many of them there are.
    /// </summary>
    [Fact]
    public async Task Concurrent_401s_from_a_superseded_revision_never_touch_the_replacement_credentials()
    {
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await using var request = fixture.Request(fixture.BusinessA);
        var handler = new FanOutHandler(
            Respond.Unauthorized,
            concurrentMachineProductRequests: MachineIds.Length,
            onEveryRequestInFlight: () => fixture.SaveCredentialAsync(
                fixture.BusinessA, OperatorA, ReplacementTokenA));
        var client = request.ClientFor(handler);

        var failures = await Task.WhenAll(MachineIds
            .Select(machineId => Record.ExceptionAsync(
                () => client.GetMachineProductsAsync(machineId, CancellationToken.None)))
            .ToList());

        Assert.All(failures, failure => Assert.Equal(
            NayaxConnectionStatus.NeedsAttention,
            Assert.IsType<NayaxNotConnectedException>(failure).Status));
        Assert.Equal(1, request.Commands.MaxConcurrentCommands);

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(2, stored.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, stored.Status);
        Assert.Null(stored.LastTestedAtUtc);
        Assert.Equal(
            MachineIds.Length,
            request.StoreLogger.Entries.Count(entry => entry.Message.Contains(
                "Stale Nayax status result discarded", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Two businesses, each fanning out in its own request over the same database: every request in a
    /// fan-out carries the credentials of the business that made it, and one business's 401 storm
    /// leaves the other's connection exactly as it was.
    ///
    /// The two fan-outs run one after the other because a request owns its own
    /// <see cref="AppDbContext"/> and its own connection in production, while these tests share one
    /// SQLite connection; what has to hold is that neither fan-out's credential reads or status
    /// writes can reach the other business's row, which the central tenant filter and the
    /// conditional update decide rather than the order the requests ran in.
    /// </summary>
    [Fact]
    public async Task Each_business_s_fan_out_uses_its_own_credentials_and_its_own_status_row()
    {
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await fixture.ConnectAsync(fixture.BusinessB, OperatorB, TokenB, NayaxConnectionStatus.Ready);

        await using var requestA = fixture.Request(fixture.BusinessA);
        var handlerA = new FanOutHandler(Respond.Catalogue, concurrentMachineProductRequests: MachineIds.Length);
        var resultA = await new CalculateReorderNeeds(
                requestA.ClientFor(handlerA),
                new EfOutstandingSupplierOrderQuantityStore(requestA.Db),
                new EfNayaxProductLinkStore(requestA.Db),
                MachineIds.Length)
            .Handle(CancellationToken.None);

        await using var requestB = fixture.Request(fixture.BusinessB);
        var handlerB = new FanOutHandler(Respond.Unauthorized, concurrentMachineProductRequests: MachineIds.Length);
        var clientB = requestB.ClientFor(handlerB);
        var failuresB = await Task.WhenAll(MachineIds
            .Select(machineId => Record.ExceptionAsync(
                () => clientB.GetMachineProductsAsync(machineId, CancellationToken.None)))
            .ToList());

        Assert.Equal(MachineIds, resultA.MachineIds);
        Assert.All(handlerA.Requests, sent => Assert.Equal($"Bearer {TokenA}", sent.Authorization));
        Assert.All(handlerB.Requests, sent => Assert.Equal($"Bearer {TokenB}", sent.Authorization));
        Assert.All(failuresB, failure => Assert.IsType<NayaxNotConnectedException>(failure));
        Assert.Equal(1, requestA.Commands.MaxConcurrentCommands);
        Assert.Equal(1, requestB.Commands.MaxConcurrentCommands);

        var storedA = await fixture.ReadRowAsync(fixture.BusinessA);
        var storedB = await fixture.ReadRowAsync(fixture.BusinessB);
        Assert.Equal((NayaxConnectionStatus.Ready, TestedAt), (storedA.Status, storedA.LastTestedAtUtc));
        Assert.Equal((NayaxConnectionStatus.NeedsAttention, Now), (storedB.Status, storedB.LastTestedAtUtc));
    }

    /// <summary>
    /// Waiting for a turn on the context must stay cancellable: a cancelled caller observes
    /// cancellation rather than queueing behind the rest of its fan-out, and writes nothing.
    /// </summary>
    [Fact]
    public async Task A_cancelled_caller_observes_cancellation_instead_of_waiting_for_the_context()
    {
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await using var request = fixture.Request(fixture.BusinessA);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Store.FindForOperationAsync(
                NayaxConnectionStatusGate.AllowsOrdinaryOperations, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.Store.TryApplyStatusResultAsync(
                new NayaxConnectionStatusResult(1, NayaxConnectionStatus.NeedsAttention, Now),
                cancelled.Token));

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(NayaxConnectionStatus.Ready, stored.Status);
        Assert.Equal(TestedAt, stored.LastTestedAtUtc);
    }

    /// <summary>
    /// The catalog reconciliation reads Nayax and the local catalogue, and both halves now reach the
    /// same scoped context: the remote half through its credential read. The two remote reads stay a
    /// concurrent fan-out; the local reads must not be in flight across them.
    /// </summary>
    [Fact]
    public async Task The_catalog_reconciliation_keeps_its_local_reads_off_the_context_during_the_remote_reads()
    {
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await fixture.AddProductAsync(fixture.BusinessA, ProductId, "Chips");
        await using var request = fixture.Request(fixture.BusinessA);
        var handler = new FanOutHandler(Respond.Catalogue, concurrentOperatorRequests: 2);
        var useCase = new GetNayaxCatalogReconciliation(
            new NayaxCatalogSnapshotProvider(request.ClientFor(handler)),
            new EfLocalCatalogSnapshotProvider(request.Db));

        var report = await useCase.Handle(CancellationToken.None);

        Assert.Equal(1, request.Commands.MaxConcurrentCommands);
        Assert.Equal(2, handler.MaxConcurrentRequests);
        var product = Assert.Single(report.Products);
        Assert.Equal(ProductId, product.ExternalId);
        Assert.Equal(MachineIds.Length, report.Machines.Count);
    }

    /// <summary>
    /// The site dashboard's bounded per-machine fan-out and its one batched completed-sales read, on
    /// the real EF facts store: the fan-out stays concurrent and the sales read is not in flight
    /// while it runs.
    /// </summary>
    [Fact]
    public async Task The_site_dashboard_keeps_its_batched_sales_read_off_the_context_during_the_fan_out()
    {
        var time = new FixedSydneyTime(Now);
        await using var fixture = await RequestFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await fixture.AddProductAsync(fixture.BusinessA, LocalProductId, "Chips");
        await fixture.RecordNayaxProductIdAsync(fixture.BusinessA, ProductId, LocalProductId);
        await using var request = fixture.Request(fixture.BusinessA);
        var handler = new FanOutHandler(Respond.Catalogue, concurrentMachineProductRequests: MachineIds.Length);
        var useCase = new GetSiteSummaries(
            request.ClientFor(handler),
            new EfSiteFactsStore(request.Db),
            new EfNayaxProductLinkStore(request.Db),
            new SiteNameResolver(),
            time.Clock,
            time.Calendar,
            TestCurrentCapabilities.Owner);

        var summaries = await useCase.Handle(CancellationToken.None);

        var site = Assert.Single(summaries);
        Assert.Equal(SiteId, site.SiteId);
        Assert.Equal(MachineIds.Length, site.MachineCount);
        Assert.Equal(1, request.Commands.MaxConcurrentCommands);
        Assert.Equal(MachineIds.Length, handler.MaxConcurrentRequests);
    }

    /// <summary>The Nayax payloads these tests answer with, keyed by the endpoint asked for.</summary>
    private static class Respond
    {
        /// <summary>The fleet, a machine's selections or the operator catalogue, as asked for.</summary>
        public static readonly Func<string, (HttpStatusCode Status, string Body)> Catalogue =
            url => (HttpStatusCode.OK, Body(url));

        /// <summary>A verdict on the credentials the request carried, for every endpoint.</summary>
        public static readonly Func<string, (HttpStatusCode Status, string Body)> Unauthorized =
            _ => (HttpStatusCode.Unauthorized, """{"message":"denied"}""");

        private static string Body(string url)
        {
            if (url.Contains("/machineProducts", StringComparison.Ordinal))
            {
                var machineId = long.Parse(url.Split('/')[^2], CultureInfo.InvariantCulture);
                return $$"""
                    [{"NayaxProductID":{{ProductId}},"MachineID":{{machineId}},"ProductName":"Chips",
                      "MDBCode":1,"PAR":10,"MissingStockByMDB":2,"RetailPrice":3.50}]
                    """;
            }

            if (url.EndsWith("/products", StringComparison.Ordinal))
            {
                return $$"""[{"NayaxProductID":{{ProductId}},"ProductName":"Chips"}]""";
            }

            return "[" + string.Join(
                ",",
                MachineIds.Select(machineId => $$"""
                    {"MachineID":{{machineId}},"CustomerID":{{SiteId}},"MachineName":"Harbour {{machineId}}"}
                    """)) + "]";
        }
    }

    /// <summary>
    /// One in-memory SQLite database with two synthetic businesses, real AES-GCM token encryption,
    /// and the simulated requests that read it. Setup runs on its own contexts, without the command
    /// recorder, so only the request under test is measured.
    /// </summary>
    private sealed class RequestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _setupOptions;

        private RequestFixture(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> setupOptions,
            int businessA,
            int businessB)
        {
            _connection = connection;
            _setupOptions = setupOptions;
            BusinessA = businessA;
            BusinessB = businessB;
        }

        public int BusinessA { get; }

        public int BusinessB { get; }

        public static async Task<RequestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var setupOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var setup = TestAppDbContext.Unrestricted(setupOptions);
            await setup.Database.EnsureCreatedAsync();
            var a = new Business { Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = Now };
            var b = new Business { Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = Now };
            setup.Businesses.AddRange(a, b);
            await setup.SaveChangesAsync();

            return new RequestFixture(connection, setupOptions, a.Id, b.Id);
        }

        /// <summary>Stores a business's credential and leaves it in <paramref name="status"/>.</summary>
        public async Task ConnectAsync(
            int businessId, string operatorId, string accessToken, NayaxConnectionStatus status)
        {
            await using var db = TestAppDbContext.For(_setupOptions, businessId);
            using var store = StoreFor(db, new CapturingLogger<EfNayaxConnectionStore>());
            var saved = await store.SaveCredentialAsync(operatorId, accessToken, CancellationToken.None);
            if (status != NayaxConnectionStatus.PendingPermissions)
            {
                Assert.True(await store.TryApplyStatusResultAsync(
                    new NayaxConnectionStatusResult(saved.CredentialRevision, status, TestedAt),
                    CancellationToken.None));
            }
        }

        /// <summary>Replaces a business's stored credential, as a separate Owner request would.</summary>
        public async Task SaveCredentialAsync(int businessId, string operatorId, string accessToken)
        {
            await using var db = TestAppDbContext.For(_setupOptions, businessId);
            using var store = StoreFor(db, new CapturingLogger<EfNayaxConnectionStore>());
            await store.SaveCredentialAsync(operatorId, accessToken, CancellationToken.None);
        }

        /// <summary>One catalogue product, so a local snapshot has something to reconcile.</summary>
        public async Task AddProductAsync(int businessId, long productId, string name)
        {
            await using var db = TestAppDbContext.For(_setupOptions, businessId);
            db.Products.Add(new Product { Id = productId, Name = name, IsActive = true });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Records the Nayax id (issue #732, on the product row since #759) that makes
        /// <paramref name="nayaxProductId"/> name the local product <paramref name="productId"/>, so
        /// the fan-outs below resolve a product the way a real request does - from the product row,
        /// on the request's own context.
        /// </summary>
        public async Task RecordNayaxProductIdAsync(int businessId, long nayaxProductId, long productId)
        {
            await using var db = TestAppDbContext.For(_setupOptions, businessId);
            var product = await db.Products.SingleAsync(row => row.Id == productId);
            product.ExternalProductId = NayaxExternalId.From(nayaxProductId);
            await db.SaveChangesAsync();
        }

        public async Task<BusinessNayaxConnection> ReadRowAsync(int businessId)
        {
            await using var db = TestAppDbContext.Unrestricted(_setupOptions);
            return await db.BusinessNayaxConnections
                .AsNoTracking()
                .SingleAsync(connection => connection.BusinessId == businessId);
        }

        /// <summary>
        /// One request's scope: one <see cref="AppDbContext"/> behind one store and one credential
        /// provider, exactly as the container composes them, with the command recorder attached to
        /// that context only.
        /// </summary>
        public SimulatedRequest Request(int businessId)
        {
            var commands = new ContextCommandRecorder();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(commands)
                .Options;
            var db = TestAppDbContext.For(options, businessId);
            var logger = new CapturingLogger<EfNayaxConnectionStore>();

            return new SimulatedRequest(db, StoreFor(db, logger), logger, commands, businessId);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

        private static EfNayaxConnectionStore StoreFor(
            AppDbContext db, CapturingLogger<EfNayaxConnectionStore> logger)
        {
            var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
            options.Keys[ActiveKeyId] = Convert.ToBase64String(
                Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

            return new EfNayaxConnectionStore(
                db, new AesGcmNayaxTokenProtector(options), new FakeClock(Now), logger);
        }
    }

    /// <summary>One simulated request: its context, its store, its provider and its recorder.</summary>
    private sealed class SimulatedRequest : IAsyncDisposable
    {
        private readonly EfNayaxConnectionStore _store;
        private readonly INayaxRequestCredentialProvider _credentials;

        public SimulatedRequest(
            AppDbContext db,
            EfNayaxConnectionStore store,
            CapturingLogger<EfNayaxConnectionStore> storeLogger,
            ContextCommandRecorder commands,
            int businessId)
        {
            Db = db;
            Store = store;
            StoreLogger = storeLogger;
            Commands = commands;
            _store = store;
            _credentials = new NayaxRequestCredentialProvider(
                store,
                TestAppDbContext.ScopeFor(businessId),
                TestCurrentPlan.Unrestricted(),
                new FakeClock(Now));
        }

        public AppDbContext Db { get; }

        public INayaxConnectionStore Store { get; }

        public CapturingLogger<EfNayaxConnectionStore> StoreLogger { get; }

        public ContextCommandRecorder Commands { get; }

        public INayaxLynxClient ClientFor(HttpMessageHandler handler) =>
            new NayaxLynxClient(
                // A deliberately unroutable host: these tests must never reach a real Nayax endpoint.
                new HttpClient(handler) { BaseAddress = new Uri("https://nayax.invalid/operational/v1/") },
                _credentials,
                new CapturingLogger<NayaxLynxClient>());

        public async ValueTask DisposeAsync()
        {
            _store.Dispose();
            await Db.DisposeAsync();
        }
    }

    /// <summary>
    /// Parks every command of one context briefly, inside EF's own critical section, and records how
    /// many were ever in flight at once. The park is what makes the context genuinely asynchronous:
    /// a caller that starts a second operation before the first completes is rejected by EF here,
    /// where SQLite's synchronous async implementation would otherwise have finished the first
    /// command before the second began. The window closes as soon as an overlap is seen, so a
    /// failing run is fast and a serialized one costs one short park per command.
    /// </summary>
    private sealed class ContextCommandRecorder : DbCommandInterceptor
    {
        private static readonly TimeSpan OverlapWindow = TimeSpan.FromMilliseconds(100);

        private readonly TaskCompletionSource _overlapSeen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maxConcurrent;
        private int _connectionCommands;

        public int MaxConcurrentCommands => Volatile.Read(ref _maxConcurrent);

        /// <summary>How many commands named the Nayax connection table.</summary>
        public int ConnectionCommandCount => Volatile.Read(ref _connectionCommands);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ParkAsync(command).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await ParkAsync(command).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await ParkAsync(command).ConfigureAwait(false);
            return result;
        }

        private async Task ParkAsync(DbCommand command)
        {
            if (command.CommandText.Contains("BusinessNayaxConnections", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _connectionCommands);
            }

            var inFlight = Interlocked.Increment(ref _inFlight);
            RecordMaximum(inFlight);
            if (inFlight > 1)
            {
                _overlapSeen.TrySetResult();
            }

            await Task.WhenAny(_overlapSeen.Task, Task.Delay(OverlapWindow)).ConfigureAwait(false);
            Interlocked.Decrement(ref _inFlight);
        }

        private void RecordMaximum(int inFlight)
        {
            var observed = Volatile.Read(ref _maxConcurrent);
            while (inFlight > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maxConcurrent, inFlight, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    /// <summary>
    /// Records a safe snapshot of each request and holds a fan-out's requests until all of them are
    /// in flight, so the credential reads that precede them have all happened and whatever has to
    /// land between those reads and their answers - a credential replacement - lands exactly once.
    /// The wait is bounded, so a caller that serialized the remote requests fails the assertion
    /// instead of hanging the suite.
    /// </summary>
    private sealed class FanOutHandler : HttpMessageHandler
    {
        private static readonly TimeSpan FanOutWait = TimeSpan.FromSeconds(10);

        private readonly Func<string, (HttpStatusCode Status, string Body)> _respond;
        private readonly string? _gatedEndpoint;
        private readonly int _expectedConcurrentRequests;
        private readonly Func<Task>? _onEveryRequestInFlight;
        private readonly TaskCompletionSource _allInFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _started;
        private int _inFlight;

        public FanOutHandler(
            Func<string, (HttpStatusCode Status, string Body)> respond,
            int concurrentMachineProductRequests = 0,
            int concurrentOperatorRequests = 0,
            Func<Task>? onEveryRequestInFlight = null)
        {
            _respond = respond;
            _gatedEndpoint = concurrentMachineProductRequests > 0 ? "/machineProducts" : null;
            _expectedConcurrentRequests = concurrentMachineProductRequests > 0
                ? concurrentMachineProductRequests
                : concurrentOperatorRequests;
            _onEveryRequestInFlight = onEveryRequestInFlight;
        }

        public List<(string Url, string? Authorization)> Requests { get; } = [];

        public int MaxConcurrentRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var gated = _expectedConcurrentRequests > 0
                && (_gatedEndpoint is null || url.Contains(_gatedEndpoint, StringComparison.Ordinal));

            bool everyRequestInFlight;
            lock (_sync)
            {
                Requests.Add((url, request.Headers.Authorization?.ToString()));
                _inFlight++;
                MaxConcurrentRequests = Math.Max(MaxConcurrentRequests, _inFlight);
                everyRequestInFlight = gated && ++_started >= _expectedConcurrentRequests;
            }

            if (gated)
            {
                if (everyRequestInFlight)
                {
                    if (_onEveryRequestInFlight is not null)
                    {
                        await _onEveryRequestInFlight().ConfigureAwait(false);
                    }

                    _allInFlight.TrySetResult();
                }
                else
                {
                    await Task.WhenAny(_allInFlight.Task, Task.Delay(FanOutWait, cancellationToken))
                        .ConfigureAwait(false);
                }
            }

            lock (_sync)
            {
                _inFlight--;
            }

            var (status, body) = _respond(url);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
