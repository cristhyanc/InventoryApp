using Inventory.Application.Nayax;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The port as the composition root registers it since issue #840: the machine-list cache in front
/// of the Lynx HTTP client. What is pinned here is everything the cache must not change - who may
/// have an answer, whose answer it is, and which calls are cached at all.
///
/// <para>Four properties, each one a thing the cache could otherwise quietly break. The connection,
/// the status gate and the support-session refusal run before <em>every</em> answer, not only before
/// the read that filled the entry. One business never receives another's list. Replacing a
/// business's credentials makes the next read go to Nayax. And the machine list is the only call
/// that is cached - everything else reaches the client untouched.</para>
/// </summary>
public class MachineListCachingNayaxLynxClientTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    // Obviously fake values. No real token or operator account belongs here.
    private const string OperatorId = "2002736764";
    private const string FakeToken = "fake-token-not-a-real-credential";

    [Fact]
    public async Task A_second_read_of_one_business_answers_from_memory()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1), Cache());

        var first = await decorated.GetMachinesAsync(CancellationToken.None);
        var second = await decorated.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(1, client.MachineListCalls);
        Assert.Equal(first.Select(machine => machine.MachineID), second.Select(machine => machine.MachineID));
    }

    /// <summary>
    /// The cache is not an access decision and must never become one: the connection, the status gate
    /// and the support-session refusal are resolved on every call, so a business whose connection has
    /// since stopped allowing ordinary operations gets today's refusal even though a list for its key
    /// is sitting in memory.
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
        var decorated = Decorate(client, credentials, Resolved(1), Cache());

        await decorated.GetMachinesAsync(CancellationToken.None);
        credentials.Refusal = refusal;

        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(() => decorated.GetMachinesAsync(CancellationToken.None)));
        Assert.Same(
            refusal,
            await Assert.ThrowsAnyAsync<Exception>(() => decorated.GetMachinesFreshAsync(CancellationToken.None)));

        // The refusal happened before the cache was consulted and before the client was reached: the
        // one read is still the one that filled the entry.
        Assert.Equal(1, client.MachineListCalls);
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
        var cache = Cache();
        var first = Decorate(client, Credentials(), Resolved(1), cache);
        var second = Decorate(client, Credentials(), Resolved(2), cache);

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
    /// Saving or replacing a connection raises the credential revision, and the revision is part of
    /// the key - so the next read goes to Nayax. That matters because new credentials may point at a
    /// different Nayax operator account entirely, whose fleet is not the one in memory.
    /// </summary>
    [Fact]
    public async Task New_credentials_make_the_next_read_go_to_nayax()
    {
        var client = new CountingNayaxLynxClient();
        var cache = Cache();
        var scope = Resolved(1);
        var before = Decorate(client, Credentials(credentialRevision: 1), scope, cache);
        var after = Decorate(client, Credentials(credentialRevision: 2), scope, cache);

        await before.GetMachinesAsync(CancellationToken.None);
        var replaced = await after.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(2L, Assert.Single(replaced).MachineID);
    }

    /// <summary>
    /// The fresh read of the five declared fresh callers - provider machine review and apply, the
    /// latest-sales sync, the cost transition preview/apply and the sale timestamp repair preview -
    /// reaches Nayax even with a warm cache, and what it read becomes what the cached callers see
    /// next.
    /// </summary>
    [Fact]
    public async Task A_fresh_read_reaches_nayax_with_a_warm_cache_and_replaces_what_is_held()
    {
        var client = new CountingNayaxLynxClient();
        var decorated = Decorate(client, Credentials(), Resolved(1), Cache());

        await decorated.GetMachinesAsync(CancellationToken.None);
        var fresh = await decorated.GetMachinesFreshAsync(CancellationToken.None);
        var cached = await decorated.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(2L, Assert.Single(fresh).MachineID);
        Assert.Equal(2L, Assert.Single(cached).MachineID);
    }

    /// <summary>
    /// The machine list is the only cached call. Machine products, stock, last sales, alerts, one
    /// machine, the product catalogue, the product groups and the devices all reach the client every
    /// time - and the decorator resolves no credential of its own for them, because the client
    /// already does that per call.
    /// </summary>
    [Fact]
    public async Task Nothing_but_the_machine_list_is_cached()
    {
        var client = new CountingNayaxLynxClient();
        var credentials = Credentials();
        var decorated = Decorate(client, credentials, Resolved(1), Cache());

        await decorated.GetMachineProductsAsync(42, CancellationToken.None);
        await decorated.GetMachineProductsAsync(42, CancellationToken.None);
        await decorated.GetProductsAsync(CancellationToken.None);
        await decorated.GetProductsAsync(CancellationToken.None);
        await decorated.GetMachineAsync(42, CancellationToken.None);
        await decorated.GetMachineAsync(42, CancellationToken.None);

        Assert.Equal(2, client.MachineProductCalls);
        Assert.Equal(2, client.ProductCalls);
        Assert.Equal(2, client.MachineCalls);
        Assert.Equal(0, credentials.Resolutions);
    }

    /// <summary>
    /// A scope with no resolved business caches nothing at all rather than sharing a key: "no
    /// business" must never become one more tenant.
    /// </summary>
    [Fact]
    public async Task A_scope_with_no_business_caches_nothing()
    {
        var client = new CountingNayaxLynxClient();
        var cache = Cache();
        var decorated = Decorate(client, Credentials(), new BusinessScope(), cache);

        await decorated.GetMachinesAsync(CancellationToken.None);
        await decorated.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, client.MachineListCalls);
        Assert.Equal(0, cache.TrackedKeys);
    }

    private static MachineListCachingNayaxLynxClient Decorate(
        INayaxLynxClient inner,
        INayaxRequestCredentialProvider credentials,
        IBusinessScope scope,
        NayaxMachineListCache cache) =>
        new(inner, credentials, scope, cache);

    // A clock that never moves: nothing here is about the ten-minute lifetime, which
    // NayaxMachineListCacheTests covers on its own.
    private static NayaxMachineListCache Cache() => new(new FakeClock(Now));

    private static FakeNayaxRequestCredentialProvider Credentials(int credentialRevision = 1) =>
        new(OperatorId, FakeToken, NayaxConnectionStatus.Ready, credentialRevision);

    private static BusinessScope Resolved(int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return scope;
    }

    /// <summary>
    /// A stand-in for <see cref="NayaxLynxClient"/> that counts what reached it and answers the
    /// machine list with one machine numbered after the read, so a test can say which read an answer
    /// came from. Its own fresh method throws: the decorator must reach the upstream through the one
    /// plain read, since a client that caches nothing answers both identically and a second path
    /// would be a second place for the behaviour to drift.
    /// </summary>
    private sealed class CountingNayaxLynxClient : INayaxLynxClient
    {
        private int _machineListCalls;
        private int _machineProductCalls;
        private int _productCalls;
        private int _machineCalls;

        public int MachineListCalls => Volatile.Read(ref _machineListCalls);

        public int MachineProductCalls => Volatile.Read(ref _machineProductCalls);

        public int ProductCalls => Volatile.Read(ref _productCalls);

        public int MachineCalls => Volatile.Read(ref _machineCalls);

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _machineListCalls);
            return Task.FromResult<List<NayaxMachine>>(
                [new NayaxMachine { MachineID = call, MachineName = $"read {call}" }]);
        }

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(
                "The decorator reads the upstream machine list through GetMachinesAsync only.");

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineProductCalls);
            return Task.FromResult(new List<NayaxMachineProduct>());
        }

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

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<NayaxDevice>());

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            Task.FromResult(products);

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<NayaxProductGroup>());

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default) =>
            Task.FromResult(new List<NayaxLastSalesReport>());

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default) =>
            Task.FromResult(new List<NayaxMachineAlert>());
    }
}
