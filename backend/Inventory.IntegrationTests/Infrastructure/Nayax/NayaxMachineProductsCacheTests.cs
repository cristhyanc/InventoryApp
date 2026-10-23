using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The machine-products cache of issue #842, on its own: what it holds, for how long, how the
/// Dashboard's simultaneous readers share one request per machine, and which result wins when two
/// reads of one key overlap.
///
/// <para>Nothing here makes an access decision - that is <see cref="CachingNayaxLynxClient"/>'s job
/// and has its own tests. These tests are about this cache's own key and its own sixty seconds over
/// the mechanism it shares with the machine-list cache
/// (<see cref="NayaxReadCache{TKey,TValue}"/>), so the "read" is a scripted function whose every call
/// is counted and can be held open on demand: a cache whose behaviour depended on timing a test
/// could not control would be a cache nobody could reason about.</para>
/// </summary>
public class NayaxMachineProductsCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    private static readonly NayaxMachineProductsCacheKey Key =
        new(BusinessId: 1, CredentialRevision: 1, MachineId: 10);

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_second_read_inside_sixty_seconds_answers_from_memory()
    {
        var clock = new FakeClock(Now);
        var cache = new NayaxMachineProductsCache(clock);
        var reader = new Reader();

        var first = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        clock.UtcNow = Now.Add(NayaxMachineProductsCache.Lifetime).AddTicks(-1);
        var second = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        AssertFromRead(1, first);
        AssertFromRead(1, second);
    }

    /// <summary>
    /// Sixty seconds, not ten minutes like the fleet list: this payload carries the machine's live
    /// <c>PAR</c> and <c>MissingStockByMDB</c>, which an operator reads to decide what to restock.
    /// </summary>
    [Fact]
    public void The_lifetime_is_sixty_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(60), NayaxMachineProductsCache.Lifetime);

    [Fact]
    public async Task The_held_list_expires_after_sixty_seconds()
    {
        var clock = new FakeClock(Now);
        var cache = new NayaxMachineProductsCache(clock);
        var reader = new Reader();

        await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        clock.UtcNow = Now.Add(NayaxMachineProductsCache.Lifetime);
        var second = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(2, reader.Calls);
        AssertFromRead(2, second);
    }

    /// <summary>
    /// Each caller gets its own list over the same selections, so a caller that sorts or trims what
    /// it was handed cannot change what the next caller reads.
    /// </summary>
    [Fact]
    public async Task Each_caller_gets_its_own_list()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader();

        var first = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        first.Clear();
        var second = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        AssertFromRead(1, second);
    }

    /// <summary>
    /// The Dashboard's shape: the summary, low stock and the sites page read one machine at the same
    /// moment, before any of them has finished. Exactly one reaches Nayax and all three answer with
    /// its result. A cache that only stored finished answers would let all three miss, which is the
    /// waste issue #842 exists to remove.
    /// </summary>
    [Fact]
    public async Task Three_simultaneous_reads_make_one_request_and_every_caller_shares_its_result()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var callers = Enumerable
            .Range(0, 3)
            .Select(_ => Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None)))
            .ToList();

        await WaitUntil(() => reader.Waiting(1), "the single upstream read to start");
        reader.Complete(1);
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, reader.Calls);
        Assert.All(results, slots => AssertFromRead(1, slots));
    }

    /// <summary>
    /// A failure is nobody's cached answer, and a cached failure would turn one bad moment into a
    /// minute of refusals. The three are the ones the Lynx client actually raises: a 401 once the
    /// connection has been marked, a 403 against credentials nothing has tested, and every other
    /// non-success status.
    /// </summary>
    [Fact]
    public async Task A_failed_read_reaches_every_caller_and_is_not_held()
    {
        await AssertFailureIsSharedAndNotHeld(
            new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention));
        await AssertFailureIsSharedAndNotHeld(
            new NayaxPermissionNotGrantedException("GetMachineProductsAsync"));
        await AssertFailureIsSharedAndNotHeld(new NayaxUpstreamException(
            "GetMachineProductsAsync",
            HttpMethod.Get,
            "machines/10/machineProducts",
            HttpStatusCode.ServiceUnavailable));
    }

    private static async Task AssertFailureIsSharedAndNotHeld(Exception failure)
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var callers = Enumerable
            .Range(0, 3)
            .Select(_ => Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None)))
            .ToList();

        await WaitUntil(() => reader.Waiting(1), "the single upstream read to start");
        reader.Fail(1, failure);

        foreach (var caller in callers)
        {
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => caller));
        }

        var next = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(2, reader.Calls);
        AssertFromRead(2, next);
    }

    [Fact]
    public async Task A_cancelled_read_cancels_the_caller_and_is_not_held()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);
        using var cancellation = new CancellationTokenSource();

        var caller = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, cancellation.Token));
        await WaitUntil(() => reader.Waiting(1), "the single upstream read to start");
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);

        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
    }

    /// <summary>
    /// Three keys that differ in one component each: another machine, another business and another
    /// credential revision. None of them is answered with any other's selections, which for the
    /// machine is what makes the cache correct at all and for the business is the only place one
    /// business could otherwise be served another's stock figures.
    /// </summary>
    [Fact]
    public async Task Another_machine_business_or_credential_revision_is_another_key()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader();
        var otherMachine = new NayaxMachineProductsCacheKey(BusinessId: 1, CredentialRevision: 1, MachineId: 11);
        var otherBusiness = new NayaxMachineProductsCacheKey(BusinessId: 2, CredentialRevision: 1, MachineId: 10);
        var newerRevision = new NayaxMachineProductsCacheKey(BusinessId: 1, CredentialRevision: 2, MachineId: 10);

        AssertFromRead(1, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(2, await cache.GetOrReadAsync(otherMachine, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(3, await cache.GetOrReadAsync(otherBusiness, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(4, await cache.GetOrReadAsync(newerRevision, reader.ReadAsync, CancellationToken.None));

        // And each one keeps its own answer rather than the last one written.
        AssertFromRead(1, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(2, await cache.GetOrReadAsync(otherMachine, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(3, await cache.GetOrReadAsync(otherBusiness, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(4, reader.Calls);
    }

    [Fact]
    public async Task A_fresh_read_ignores_what_is_held_and_replaces_it()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader();

        await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        var fresh = await cache.ReadFreshAsync(Key, reader.ReadAsync, CancellationToken.None);
        var afterwards = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(2, reader.Calls);
        AssertFromRead(2, fresh);
        AssertFromRead(2, afterwards);
    }

    [Fact]
    public async Task A_failed_fresh_read_leaves_what_is_held_alone()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [2]);
        var failure = Upstream();

        await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        var fresh = Task.Run(() => cache.ReadFreshAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(2), "the fresh read to start");
        reader.Fail(2, failure);

        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => fresh));
        AssertFromRead(1, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(2, reader.Calls);
    }

    /// <summary>
    /// The ordering rule, and the reason it is written as a sequence number rather than "last write
    /// wins": results arrive in whatever order Nayax answers them. A read that started before a fresh
    /// read must not store its older stock figures over the fresher ones just because it finished
    /// afterwards.
    /// </summary>
    [Fact]
    public async Task An_older_read_still_running_never_overwrites_a_newer_fresh_read()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var older = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the older read to start");

        AssertFromRead(2, await cache.ReadFreshAsync(Key, reader.ReadAsync, CancellationToken.None));

        reader.Complete(1);

        // The caller that started first still gets the list it was answered with - it asked, and this
        // is the answer it got. What must not happen is that answer replacing the newer one.
        AssertFromRead(1, await older);
        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(2, reader.Calls);
    }

    /// <summary>
    /// The same rule when the older read is the one that finishes first: the fresh read started after
    /// it, so the fresh read's selections are the newer news whichever order the two arrive in.
    /// </summary>
    [Fact]
    public async Task An_older_read_that_finishes_first_is_still_not_the_answer_after_a_fresh_read()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1, 2]);

        var older = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the older read to start");

        var fresh = Task.Run(() => cache.ReadFreshAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(2), "the fresh read to start");

        reader.Complete(1);
        AssertFromRead(1, await older);
        reader.Complete(2);
        AssertFromRead(2, await fresh);

        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task An_older_read_still_running_never_overwrites_an_invalidation()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var older = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the older read to start");

        cache.Invalidate(Key);
        reader.Complete(1);
        AssertFromRead(1, await older);

        // Nothing is held, so the next caller reads again rather than being handed selections the
        // invalidation - a machine-product write, in production - already decided were superseded.
        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
    }

    /// <summary>
    /// An invalidation has to reach the read that is still running, not only the list already held. A
    /// caller that arrives while that read is open would otherwise join it and be answered with
    /// exactly the pre-invalidation selections - after a write, the very ones that write replaced.
    /// </summary>
    [Fact]
    public async Task A_caller_after_an_invalidation_does_not_join_the_read_that_was_already_running()
    {
        var cache = new NayaxMachineProductsCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var before = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the read before the invalidation to start");

        cache.Invalidate(Key);
        var after = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));

        // Its own read, and its own answer, while the first read is still open. Awaited with a limit
        // on purpose: a caller that joined the barred read would sit here until that read is
        // released, and this has to fail as a statement about joining rather than as a hung test.
        AssertFromRead(2, await after.WaitAsync(WaitLimit));
        Assert.Equal(2, reader.Calls);

        reader.Complete(1);
        AssertFromRead(1, await before);

        // And what the later read brought back is what is held: the barred one cannot replace it.
        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(2, reader.Calls);
    }

    /// <summary>
    /// Keys are forgotten rather than accumulated - one per machine here, so a fleet occupies as many
    /// as it has machines - and only the ones holding nothing usable: the clean-up is about memory and
    /// must never make a live key answer differently.
    /// </summary>
    [Fact]
    public async Task Expired_keys_are_forgotten_once_too_many_are_remembered()
    {
        var clock = new FakeClock(Now);
        var cache = new NayaxMachineProductsCache(clock);
        var reader = new Reader();

        for (var machine = 1; machine <= NayaxMachineProductsCache.MaxTrackedKeys; machine++)
        {
            await cache.GetOrReadAsync(
                new NayaxMachineProductsCacheKey(BusinessId: 1, CredentialRevision: 1, MachineId: machine),
                reader.ReadAsync,
                CancellationToken.None);
        }

        Assert.Equal(NayaxMachineProductsCache.MaxTrackedKeys, cache.TrackedKeys);

        clock.UtcNow = Now.Add(NayaxMachineProductsCache.Lifetime);
        var survivor = new NayaxMachineProductsCacheKey(BusinessId: 1, CredentialRevision: 1, MachineId: 9_000);
        await cache.GetOrReadAsync(survivor, reader.ReadAsync, CancellationToken.None);

        Assert.True(
            cache.TrackedKeys <= NayaxMachineProductsCache.MaxTrackedKeys,
            $"Expected at most {NayaxMachineProductsCache.MaxTrackedKeys} tracked keys, found {cache.TrackedKeys}.");

        // The key that is still live kept its list: clearing out expired entries is not an
        // invalidation of everything.
        var callsBefore = reader.Calls;
        await cache.GetOrReadAsync(survivor, reader.ReadAsync, CancellationToken.None);
        Assert.Equal(callsBefore, reader.Calls);
    }

    private static NayaxUpstreamException Upstream() => new(
        "GetMachineProductsAsync",
        HttpMethod.Get,
        "machines/10/machineProducts",
        HttpStatusCode.BadGateway);

    private static void AssertFromRead(int read, List<NayaxMachineProduct> slots) =>
        Assert.Equal($"read {read}", Assert.Single(slots).ProductName);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > WaitLimit)
            {
                Assert.Fail($"Timed out after {WaitLimit.TotalSeconds:0}s waiting for {what}.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A scripted stand-in for one machine's selections read. Every call is numbered and counted, and
    /// a call named in <c>blockedCalls</c> waits until the test completes or fails it - which is what
    /// makes "two reads overlap" something a test can state rather than hope for.
    /// </summary>
    private sealed class Reader
    {
        private readonly HashSet<int> _blockedCalls;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<List<NayaxMachineProduct>>> _gates = new();
        private int _calls;

        public Reader(params int[] blockedCalls) => _blockedCalls = [.. blockedCalls];

        public int Calls => Volatile.Read(ref _calls);

        public Task<List<NayaxMachineProduct>> ReadAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            return _blockedCalls.Contains(call)
                ? Gate(call).Task.WaitAsync(cancellationToken)
                : Task.FromResult(Slots(call));
        }

        /// <summary>Whether the numbered call has started and is waiting to be released.</summary>
        public bool Waiting(int call) => Calls >= call && _gates.ContainsKey(call);

        public void Complete(int call) => Gate(call).TrySetResult(Slots(call));

        public void Fail(int call, Exception failure) => Gate(call).TrySetException(failure);

        private TaskCompletionSource<List<NayaxMachineProduct>> Gate(int call) =>
            _gates.GetOrAdd(
                call,
                _ => new TaskCompletionSource<List<NayaxMachineProduct>>(
                    TaskCreationOptions.RunContinuationsAsynchronously));

        // One selection per read, named after the read it came from, so a test can say which read an
        // answer came from instead of only how many reads happened.
        private static List<NayaxMachineProduct> Slots(int call) =>
        [
            new NayaxMachineProduct
            {
                NayaxProductID = call,
                MachineID = 10,
                ProductName = $"read {call}",
                PAR = 10,
                MissingStockByMDB = call,
            },
        ];
    }
}
