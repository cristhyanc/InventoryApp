using System.Net;
using System.Text;
using Inventory.Application.Nayax;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Http;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The port as the composition root registers it since issues #840 and #842: the machine-list cache
/// and the machine-products cache in front of the Lynx HTTP client. What is pinned here is everything
/// the caches must not change - who may have an answer, whose answer it is, and which calls are
/// cached at all.
///
/// <para>Each property is a thing a cache could otherwise quietly break. The connection, the status
/// gate and the support-session refusal run before <em>every</em> answer, not only before the read
/// that filled the entry. One business never receives another's data, and one machine never receives
/// another machine's selections. Replacing a business's credentials makes the next read go to Nayax,
/// and the key names the credentials the request actually carried rather than ones resolved a moment
/// earlier - proved through the real <see cref="NayaxLynxClient"/>. A machine-product write forgets
/// that machine's entry, and only when it succeeded. And those two reads are the only cached ones -
/// everything else reaches the client untouched.</para>
/// </summary>
public class CachingNayaxLynxClientTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    private const long MachineId = 42;
    private const long OtherMachineId = 43;

    // Obviously fake values. No real token or operator account belongs here.
    private const string OperatorId = "2002736764";
    private const string FakeToken = "fake-token-not-a-real-credential";

    [Fact]
    public async Task A_second_read_of_one_business_answers_from_memory()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));

        var first = await decorated.GetMachinesAsync(CancellationToken.None);
        var second = await decorated.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(1, client.MachineListCalls);
        Assert.Equal(first.Select(machine => machine.MachineID), second.Select(machine => machine.MachineID));
    }

    /// <summary>
    /// The read issue #842 is about: the Dashboard's summary, low stock and sites page each read every
    /// machine's selections, so a second read of one machine inside the minute answers from memory
    /// instead of spending another request.
    /// </summary>
    [Fact]
    public async Task A_second_read_of_one_machines_products_answers_from_memory()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));

        var first = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var second = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);

        Assert.Equal(1, client.MachineProductReads);
        Assert.Equal("read 1", Assert.Single(first).ProductName);
        Assert.Equal("read 1", Assert.Single(second).ProductName);
    }

    /// <summary>
    /// The cache is not an access decision and must never become one: the connection, the status gate
    /// and the support-session refusal are resolved on every call, so a business whose connection has
    /// since stopped allowing ordinary operations gets today's refusal even though an answer for its
    /// key is sitting in memory.
    /// </summary>
    [Fact]
    public async Task Every_refusal_still_happens_before_a_cache_hit()
    {
        await AssertRefusedWithAWarmCache(
            new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention));
        await AssertRefusedWithAWarmCache(
            new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured));
        await AssertRefusedWithAWarmCache(new SupportSessionNayaxUnavailableException());
    }

    private static async Task AssertRefusedWithAWarmCache(Exception refusal)
    {
        var client = new CountingNayaxLynxClient();
        var credentials = Credentials();
        var decorated = Decorate(client, credentials, Resolved(1));

        await decorated.GetMachinesAsync(CancellationToken.None);
        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        credentials.Refusal = refusal;

        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(() => decorated.GetMachinesAsync(CancellationToken.None)));
        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(() => decorated.GetMachinesFreshAsync(CancellationToken.None)));
        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(
                () => decorated.GetMachineProductsAsync(MachineId, CancellationToken.None)));
        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(
                () => decorated.GetMachineProductsFreshAsync(MachineId, CancellationToken.None)));
        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(
                () => decorated.CreateMachineProductsAsync(MachineId, [], CancellationToken.None)));

        // Each refusal happened before the cache was consulted and before the client was reached: the
        // one read of each is still the one that filled the entry, and no write left the API.
        Assert.Equal(1, client.MachineListCalls);
        Assert.Equal(1, client.MachineProductReads);
        Assert.Equal(0, client.MachineProductWrites);
    }

    /// <summary>
    /// Two businesses, one process-wide cache, and no possibility of crossing: the key names the
    /// business, which is resolved from the request's own scope rather than from anything a caller
    /// passed in.
    /// </summary>
    [Fact]
    public async Task Two_businesses_never_receive_each_others_list()
    {
        var client = new CountingNayaxLynxClient();
        var caches = Caches();
        var first = Decorate(client, Credentials(), Resolved(1), caches);
        var second = Decorate(client, Credentials(), Resolved(2), caches);

        var firstFleet = await first.GetMachinesAsync(CancellationToken.None);
        var secondFleet = await second.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(1L, Assert.Single(firstFleet).MachineID);
        Assert.Equal(2L, Assert.Single(secondFleet).MachineID);

        // And each business keeps answering with its own.
        Assert.Equal(1L, Assert.Single(await first.GetMachinesAsync(CancellationToken.None)).MachineID);
        Assert.Equal(2L, Assert.Single(await second.GetMachinesAsync(CancellationToken.None)).MachineID);
        Assert.Equal(2, client.MachineListCalls);
    }

    /// <summary>
    /// The same for the selections, which is where it matters most: two operators' fleets may hold the
    /// same machine number, and these are stock figures an operator restocks from.
    /// </summary>
    [Fact]
    public async Task Two_businesses_never_receive_each_others_machine_products()
    {
        var client = new CountingNayaxLynxClient();
        var caches = Caches();
        var first = Decorate(client, Credentials(), Resolved(1), caches);
        var second = Decorate(client, Credentials(), Resolved(2), caches);

        var firstSlots = await first.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var secondSlots = await second.GetMachineProductsAsync(MachineId, CancellationToken.None);

        Assert.Equal(2, client.MachineProductReads);
        Assert.Equal("read 1", Assert.Single(firstSlots).ProductName);
        Assert.Equal("read 2", Assert.Single(secondSlots).ProductName);

        Assert.Equal(
            "read 1",
            Assert.Single(await first.GetMachineProductsAsync(MachineId, CancellationToken.None)).ProductName);
        Assert.Equal(
            "read 2",
            Assert.Single(await second.GetMachineProductsAsync(MachineId, CancellationToken.None)).ProductName);
        Assert.Equal(2, client.MachineProductReads);
    }

    /// <summary>
    /// One entry per machine. A cache keyed only by the business would answer every machine with the
    /// first machine's selections, which is the one way this change could silently corrupt a stock
    /// figure.
    /// </summary>
    [Fact]
    public async Task Each_machine_has_its_own_entry()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));

        var first = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var other = await decorated.GetMachineProductsAsync(OtherMachineId, CancellationToken.None);

        Assert.Equal(2, client.MachineProductReads);
        Assert.Equal(MachineId, Assert.Single(first).MachineID);
        Assert.Equal(OtherMachineId, Assert.Single(other).MachineID);

        // And each machine keeps its own.
        Assert.Equal(
            MachineId,
            Assert.Single(await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None)).MachineID);
        Assert.Equal(
            OtherMachineId,
            Assert.Single(await decorated.GetMachineProductsAsync(OtherMachineId, CancellationToken.None)).MachineID);
        Assert.Equal(2, client.MachineProductReads);
    }

    /// <summary>
    /// Saving or replacing a connection raises the credential revision, and the revision is part of
    /// both keys - so the next read goes to Nayax. That matters because new credentials may point at a
    /// different Nayax operator account entirely, whose machines are not the ones in memory.
    /// </summary>
    [Fact]
    public async Task New_credentials_make_the_next_read_go_to_nayax()
    {
        var client = new CountingNayaxLynxClient();
        var caches = Caches();
        var scope = Resolved(1);
        var before = Decorate(client, Credentials(credentialRevision: 1), scope, caches);
        var after = Decorate(client, Credentials(credentialRevision: 2), scope, caches);

        await before.GetMachinesAsync(CancellationToken.None);
        await before.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var replacedFleet = await after.GetMachinesAsync(CancellationToken.None);
        var replacedSlots = await after.GetMachineProductsAsync(MachineId, CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(2, client.MachineProductReads);
        Assert.Equal(2L, Assert.Single(replacedFleet).MachineID);
        Assert.Equal("read 2", Assert.Single(replacedSlots).ProductName);
    }

    /// <summary>
    /// The fresh fleet read of the five declared fresh callers - provider machine review and apply,
    /// the latest-sales sync, the cost transition preview/apply and the sale timestamp repair preview -
    /// reaches Nayax even with a warm cache, and what it read becomes what the cached callers see
    /// next.
    /// </summary>
    [Fact]
    public async Task A_fresh_read_reaches_nayax_with_a_warm_cache_and_replaces_what_is_held()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));

        await decorated.GetMachinesAsync(CancellationToken.None);
        var fresh = await decorated.GetMachinesFreshAsync(CancellationToken.None);
        var cached = await decorated.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(2L, Assert.Single(fresh).MachineID);
        Assert.Equal(2L, Assert.Single(cached).MachineID);
    }

    /// <summary>
    /// A fresh fleet read reports the Nayax account it was authenticated as (issue #978), taken from
    /// the one credential this decorator resolved and sent the request with - and resolving it still
    /// costs exactly one connection read, which is the property the cache key already depends on.
    ///
    /// <para>That series is what the connected-machine snapshot is stamped with, so it has to come
    /// from the credential the request carried rather than from a later look at the stored
    /// connection: an Owner can save a different Nayax account while the request is in flight.</para>
    /// </summary>
    [Fact]
    public async Task A_fresh_fleet_read_reports_the_account_series_it_was_authenticated_as()
    {
        var client = new CountingNayaxLynxClient();
        var credentials = Credentials();
        var decorated = Decorate(client, credentials, Resolved(1));

        var fleet = await decorated.ReadFleetFreshAsync(CancellationToken.None);

        Assert.Equal(1, fleet.AccountSeries);
        Assert.Equal(1L, Assert.Single(fleet.Machines).MachineID);
        Assert.Equal(1, credentials.Resolutions);
        Assert.Equal(1, client.MachineListCalls);
    }

    /// <summary>
    /// And the same for the selections' four declared fresh callers - the pick list, Sync Restock, the
    /// machine-stock observation source (the ledger and the stock counts) and the costing transition
    /// preview: each reaches Nayax with a warm cache, and the figures it read become what the cached
    /// callers see next.
    /// </summary>
    [Fact]
    public async Task A_fresh_machine_products_read_reaches_nayax_and_replaces_what_is_held()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));

        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var fresh = await decorated.GetMachineProductsFreshAsync(MachineId, CancellationToken.None);
        var cached = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);

        Assert.Equal(2, client.MachineProductReads);
        Assert.Equal("read 2", Assert.Single(fresh).ProductName);
        Assert.Equal("read 2", Assert.Single(cached).ProductName);
    }

    /// <summary>
    /// A write replaces the machine's selections in Nayax, so what is held for that machine is no
    /// longer what the machine has: the entry is forgotten and the next cached caller reads again.
    /// Another machine's entry is untouched - the write said nothing about it.
    /// </summary>
    [Fact]
    public async Task A_successful_write_forgets_that_machines_cached_products_and_no_others()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));

        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineProductsAsync(OtherMachineId, CancellationToken.None);
        await decorated.CreateMachineProductsAsync(MachineId, [], CancellationToken.None);

        var rewritten = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var untouched = await decorated.GetMachineProductsAsync(OtherMachineId, CancellationToken.None);

        Assert.Equal(1, client.MachineProductWrites);
        Assert.Equal(3, client.MachineProductReads);
        Assert.Equal("read 3", Assert.Single(rewritten).ProductName);
        Assert.Equal("read 2", Assert.Single(untouched).ProductName);
    }

    /// <summary>
    /// A write that failed changed nothing in Nayax, so what is held is still the machine's
    /// selections: forgetting it would spend a request to re-read the same answer, and the failure
    /// reaches the caller exactly as it does today.
    /// </summary>
    [Fact]
    public async Task A_failed_write_leaves_what_is_held_alone_and_the_failure_reaches_the_caller()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1));
        var failure = new NayaxUpstreamException(
            "CreateMachineProductsAsync",
            HttpMethod.Post,
            $"machines/{MachineId}/machineProducts",
            HttpStatusCode.BadGateway);
        client.WriteFailure = failure;

        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);

        Assert.Same(
            failure,
            await Assert.ThrowsAnyAsync<Exception>(
                () => decorated.CreateMachineProductsAsync(MachineId, [], CancellationToken.None)));

        Assert.Equal(
            "read 1",
            Assert.Single(await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None)).ProductName);
        Assert.Equal(1, client.MachineProductReads);
    }

    /// <summary>
    /// The machine list and the machine products are the only cached calls. Stock, last sales, alerts,
    /// one machine, the product catalogue, the product groups and the devices all reach the client
    /// every time - and the decorator resolves no credential of its own for them, because the client
    /// already does that per call.
    /// </summary>
    [Fact]
    public async Task Nothing_but_the_machine_list_and_the_machine_products_is_cached()
    {
        var client = new CountingNayaxLynxClient();
        var credentials = Credentials();
        var decorated = Decorate(client, credentials, Resolved(1));

        await decorated.GetProductsAsync(CancellationToken.None);
        await decorated.GetProductsAsync(CancellationToken.None);
        await decorated.GetMachineAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineLastSalesAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineLastSalesAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineLastAlertsAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineLastAlertsAsync(MachineId, CancellationToken.None);
        await decorated.GetProductGroupssAsync(CancellationToken.None);
        await decorated.GetDevicesAsync(CancellationToken.None);

        Assert.Equal(2, client.ProductCalls);
        Assert.Equal(2, client.MachineCalls);
        Assert.Equal(2, client.LastSalesCalls);
        Assert.Equal(2, client.LastAlertsCalls);
        Assert.Equal(0, credentials.Resolutions);
    }

    /// <summary>
    /// One credential is resolved per cached call - including a call answered from memory, because
    /// that resolution is the refusal - and the read or write is made with that very credential, so
    /// the revision in the key is the revision the request carried rather than one it was assumed to
    /// have carried. One resolution per call is also exactly what the request's connection reads cost
    /// today (<see cref="NayaxParallelCredentialAccessTests"/>).
    /// </summary>
    [Fact]
    public async Task One_credential_is_resolved_per_call_and_the_read_carries_it()
    {
        var client = new CountingNayaxLynxClient();
        var credentials = Credentials(credentialRevision: 7);
        var decorated = Decorate(client, credentials, Resolved(1));

        await decorated.GetMachinesAsync(CancellationToken.None);
        await decorated.GetMachinesAsync(CancellationToken.None);
        await decorated.GetMachinesFreshAsync(CancellationToken.None);
        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineProductsFreshAsync(MachineId, CancellationToken.None);
        await decorated.CreateMachineProductsAsync(MachineId, [], CancellationToken.None);

        Assert.Equal(7, credentials.Resolutions);
        Assert.Equal(new[] { 7, 7 }, client.FleetReadRevisions);
        Assert.Equal(new[] { 7, 7 }, client.MachineProductReadRevisions);
        Assert.Equal(new[] { 7 }, client.MachineProductWriteRevisions);
    }

    /// <summary>
    /// The window a two-step resolution left open, through the real <see cref="NayaxLynxClient"/>:
    /// the decorator read the revision for the key and the client then resolved its own credential
    /// for the request - the provider caches nothing, deliberately - so a connection saved between
    /// the two stored the new credentials' data under the old revision's key.
    ///
    /// <para>The provider here answers the first resolution with revision 1 and every later one with
    /// revision 2, which is that save at an exact, repeatable moment. What must hold is that each
    /// answer was fetched with the credentials its key names: revision 1's selections came from the
    /// request that carried revision 1's token, and the list served from memory afterwards is
    /// revision 2's own.</para>
    /// </summary>
    [Fact]
    public async Task The_key_names_the_credentials_the_request_actually_carried()
    {
        var credentials = new RevisionAdvancingCredentialProvider(
            new NayaxConnectionCredential(OperatorId, $"{FakeToken}-1", 1),
            new NayaxConnectionCredential(OperatorId, $"{FakeToken}-2", 2));
        var handler = new SelectionsPerRequestHandler();

        // A deliberately unroutable host: this test must never reach a real Nayax endpoint.
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nayax.invalid/operational/v1/"),
        };
        var decorated = Decorate(
            new NayaxLynxClient(http, credentials, new CapturingLogger<NayaxLynxClient>()),
            credentials,
            Resolved(1));

        var underRevisionOne = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var underRevisionTwo = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        var fromMemory = await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);

        // One resolution per call, and each request carried the token of the revision that call
        // resolved - never a second snapshot read after the key had been decided.
        Assert.Equal(3, credentials.Resolutions);
        Assert.Equal(
            new[] { $"Bearer {FakeToken}-1", $"Bearer {FakeToken}-2" },
            handler.Authorizations);

        // So revision 1's answer is the selections revision 1's token fetched, revision 2 read Nayax
        // again rather than being handed them, and the third call is revision 2's own from memory.
        Assert.Equal("selections 1", Assert.Single(underRevisionOne).ProductName);
        Assert.Equal("selections 2", Assert.Single(underRevisionTwo).ProductName);
        Assert.Equal("selections 2", Assert.Single(fromMemory).ProductName);

        // And the request really was the documented per-machine endpoint.
        Assert.All(handler.Paths, path => Assert.EndsWith($"machines/{MachineId}/machineProducts", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// A scope with no resolved business caches nothing at all rather than sharing a key: "no
    /// business" must never become one more tenant.
    /// </summary>
    [Fact]
    public async Task A_scope_with_no_business_caches_nothing()
    {
        var client = new CountingNayaxLynxClient();
        var caches = Caches();
        var decorated = Decorate(client, Credentials(), new BusinessScope(), caches);

        await decorated.GetMachinesAsync(CancellationToken.None);
        await decorated.GetMachinesAsync(CancellationToken.None);
        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        await decorated.GetMachineProductsAsync(MachineId, CancellationToken.None);
        await decorated.CreateMachineProductsAsync(MachineId, [], CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(2, client.MachineProductReads);
        Assert.Equal(0, caches.MachineLists.TrackedKeys);
        Assert.Equal(0, caches.MachineProducts.TrackedKeys);
    }

    private static CachingNayaxLynxClient Decorate(
        INayaxLynxCachedReadClient inner,
        INayaxRequestCredentialProvider credentials,
        IBusinessScope scope,
        CacheSet? caches = null)
    {
        var set = caches ?? Caches();

        return new CachingNayaxLynxClient(inner, credentials, scope, set.MachineLists, set.MachineProducts);
    }

    // Clocks that never move: nothing here is about either lifetime, which
    // NayaxMachineListCacheTests and NayaxMachineProductsCacheTests cover on their own.
    private static CacheSet Caches() =>
        new(new NayaxMachineListCache(new FakeClock(Now)), new NayaxMachineProductsCache(new FakeClock(Now)));

    private static FakeNayaxRequestCredentialProvider Credentials(int credentialRevision = 1) =>
        new(OperatorId, FakeToken, NayaxConnectionStatus.Ready, credentialRevision);

    private static BusinessScope Resolved(int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return scope;
    }

    /// <summary>The two process-wide caches one decorator is built over.</summary>
    private sealed record CacheSet(NayaxMachineListCache MachineLists, NayaxMachineProductsCache MachineProducts);

    /// <summary>
    /// A stand-in for <see cref="NayaxLynxClient"/> that counts what reached it and answers each read
    /// with one row numbered after that read, so a test can say which read an answer came from. It
    /// also records the credential revision each read and write was made with.
    ///
    /// <para>The port's own machine-list and machine-product members throw. The decorator must reach
    /// the upstream through <see cref="INayaxLynxCachedReadClient"/>'s members and nothing else: those
    /// are the calls that carry the credential the decorator itself resolved and gated, and a
    /// decorator that reached a member resolving its own credential would be keying an answer by a
    /// revision it only assumed the request had used - and would cost the business a second connection
    /// read per call.</para>
    /// </summary>
    private sealed class CountingNayaxLynxClient : INayaxLynxCachedReadClient
    {
        private int _machineListCalls;
        private int _machineProductReads;
        private int _machineProductWrites;
        private int _productCalls;
        private int _machineCalls;
        private int _lastSalesCalls;
        private int _lastAlertsCalls;

        public int MachineListCalls => Volatile.Read(ref _machineListCalls);

        public int MachineProductReads => Volatile.Read(ref _machineProductReads);

        public int MachineProductWrites => Volatile.Read(ref _machineProductWrites);

        public int ProductCalls => Volatile.Read(ref _productCalls);

        public int MachineCalls => Volatile.Read(ref _machineCalls);

        public int LastSalesCalls => Volatile.Read(ref _lastSalesCalls);

        public int LastAlertsCalls => Volatile.Read(ref _lastAlertsCalls);

        /// <summary>What a write throws, or <c>null</c> to let it succeed.</summary>
        public Exception? WriteFailure { get; set; }

        /// <summary>The credential revisions the fleet reads were made with, in order.</summary>
        public List<int> FleetReadRevisions { get; } = new();

        /// <summary>The credential revisions the selections reads were made with, in order.</summary>
        public List<int> MachineProductReadRevisions { get; } = new();

        /// <summary>The credential revisions the selections writes were made with, in order.</summary>
        public List<int> MachineProductWriteRevisions { get; } = new();

        public Task<List<NayaxMachine>> ReadMachinesAsync(
            NayaxRequestCredential credential, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _machineListCalls);
            Record(FleetReadRevisions, credential);

            return Task.FromResult<List<NayaxMachine>>(
                [new NayaxMachine { MachineID = call, MachineName = $"read {call}" }]);
        }

        public Task<List<NayaxMachineProduct>> ReadMachineProductsAsync(
            long machineId, NayaxRequestCredential credential, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _machineProductReads);
            Record(MachineProductReadRevisions, credential);

            return Task.FromResult<List<NayaxMachineProduct>>(
            [
                new NayaxMachineProduct
                {
                    NayaxProductID = call,
                    MachineID = machineId,
                    ProductName = $"read {call}",
                    PAR = 10,
                    MissingStockByMDB = call,
                },
            ]);
        }

        public Task<List<NayaxMachineProduct>> WriteMachineProductsAsync(
            long machineId,
            List<NayaxMachineProduct> products,
            NayaxRequestCredential credential,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineProductWrites);
            Record(MachineProductWriteRevisions, credential);

            return WriteFailure is { } failure
                ? Task.FromException<List<NayaxMachineProduct>>(failure)
                : Task.FromResult(products);
        }

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(CredentialMessage);

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(CredentialMessage);

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(CredentialMessage);

        public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(CredentialMessage);

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            throw new NotSupportedException(CredentialMessage);

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _productCalls);
            return Task.FromResult(new List<NayaxProduct>());
        }

        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineCalls);
            return Task.FromResult(new NayaxMachine { MachineID = machineId });
        }

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _lastSalesCalls);
            return Task.FromResult(new List<NayaxLastSalesReport>());
        }

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _lastAlertsCalls);
            return Task.FromResult(new List<NayaxMachineAlert>());
        }

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<NayaxDevice>());

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<NayaxProductGroup>());

        private const string CredentialMessage =
            "The decorator reaches the upstream machine list, machine products and machine-product "
            + "write through the members that take the credential it resolved and gated itself.";

        private static void Record(List<int> revisions, NayaxRequestCredential credential)
        {
            lock (revisions)
            {
                revisions.Add(credential.CredentialRevision);
            }
        }
    }

    /// <summary>
    /// A credential provider that answers each resolution with the next credential it was given and
    /// repeats the last one from then on. That is a connection save landing between two calls at a
    /// moment a test can state: the real provider caches nothing, so each resolution is its own read
    /// of the stored row and may legitimately see a newer revision than the one before it.
    /// </summary>
    private sealed class RevisionAdvancingCredentialProvider : INayaxRequestCredentialProvider
    {
        private readonly NayaxConnectionCredential[] _credentials;
        private int _resolutions;

        public RevisionAdvancingCredentialProvider(params NayaxConnectionCredential[] credentials) =>
            _credentials = credentials;

        public int Resolutions => Volatile.Read(ref _resolutions);

        public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken)
        {
            var resolution = Interlocked.Increment(ref _resolutions);
            var credential = _credentials[Math.Min(resolution, _credentials.Length) - 1];

            return Task.FromResult(new NayaxRequestCredential(credential, NayaxConnectionStatus.Ready));
        }

        public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Answers every machine-products request with one selection named after the request number, and
    /// records the <c>Authorization</c> header and path each one carried, so a test can say which
    /// credentials fetched the selections it was handed.
    /// </summary>
    private sealed class SelectionsPerRequestHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();

        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Paths.Add(request.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    [{ "NayaxProductID": 1, "MachineID": {{MachineId}}, "ProductName": "selections {{Authorizations.Count}}",
                       "PAR": 10, "MissingStockByMDB": 2 }]
                    """,
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request,
            });
        }
    }
}
