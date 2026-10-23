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
/// The machine-list cache of issue #840, on its own: what it holds, for how long, how simultaneous
/// readers share one request, and which result wins when two reads of one key overlap.
///
/// <para>Nothing here makes an access decision - that is
/// <see cref="MachineListCachingNayaxLynxClient"/>'s job and has its own tests. These tests are
/// about the mechanism, so the "read" is a scripted function whose every call is counted and can be
/// held open on demand: a cache whose behaviour depended on timing that a test could not control
/// would be a cache nobody could reason about.</para>
/// </summary>
public class NayaxMachineListCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);
    private static readonly NayaxMachineListCacheKey Key = new(BusinessId: 1, CredentialRevision: 1);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_second_read_inside_ten_minutes_answers_from_memory()
    {
        var clock = new FakeClock(Now);
        var cache = new NayaxMachineListCache(clock);
        var reader = new Reader();

        var first = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        clock.UtcNow = Now.Add(NayaxMachineListCache.Lifetime).AddTicks(-1);
        var second = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        AssertFromRead(1, first);
        AssertFromRead(1, second);
    }

    /// <summary>
    /// Each caller gets its own list over the same machines, so a caller that sorts or trims what it
    /// was handed - several of them do - cannot change what the next caller reads.
    /// </summary>
    [Fact]
    public async Task Each_caller_gets_its_own_list()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader();

        var first = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        first.Clear();
        var second = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        AssertFromRead(1, second);
    }

    [Fact]
    public async Task The_held_list_expires_after_ten_minutes()
    {
        var clock = new FakeClock(Now);
        var cache = new NayaxMachineListCache(clock);
        var reader = new Reader();

        await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);
        clock.UtcNow = Now.Add(NayaxMachineListCache.Lifetime);
        var second = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        Assert.Equal(2, reader.Calls);
        AssertFromRead(2, second);
    }

    /// <summary>
    /// The Dashboard's shape: five reads of one key starting at the same moment, before any of them
    /// has finished. Exactly one reaches the upstream and all five answer with its result. A cache
    /// that only stored finished answers would let all five miss, which is the waste issue #840
    /// exists to remove.
    /// </summary>
    [Fact]
    public async Task Simultaneous_reads_make_one_request_and_every_caller_shares_its_result()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var callers = Enumerable
            .Range(0, 5)
            .Select(_ => Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None)))
            .ToList();

        await WaitUntil(() => reader.Waiting(1), "the single upstream read to start");
        reader.Complete(1);
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, reader.Calls);
        Assert.All(results, machines => AssertFromRead(1, machines));
    }

    /// <summary>
    /// A failure is nobody's cached answer. Every caller that was waiting sees the one the read
    /// produced, and the next read goes to the upstream again rather than being refused for ten
    /// minutes. The three failures are the ones the Lynx client actually raises: a 401 once the
    /// connection has been marked, a 403 against credentials nothing has tested, and every other
    /// non-success status.
    /// </summary>
    [Fact]
    public async Task A_failed_read_reaches_every_caller_and_is_not_held()
    {
        await AssertFailureIsSharedAndNotHeld(
            new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention));
        await AssertFailureIsSharedAndNotHeld(
            new NayaxPermissionNotGrantedException("GetMachinesAsync"));
        await AssertFailureIsSharedAndNotHeld(new NayaxUpstreamException(
            "GetMachinesAsync", HttpMethod.Get, "machines", HttpStatusCode.ServiceUnavailable));
    }

    private static async Task AssertFailureIsSharedAndNotHeld(Exception failure)
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
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
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);
        using var cancellation = new CancellationTokenSource();

        var caller = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, cancellation.Token));
        await WaitUntil(() => reader.Waiting(1), "the single upstream read to start");
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);

        var next = await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None);

        AssertFromRead(2, next);
    }

    [Fact]
    public async Task Two_keys_never_answer_with_each_others_list()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader();
        var other = new NayaxMachineListCacheKey(BusinessId: 2, CredentialRevision: 1);
        var newerRevision = new NayaxMachineListCacheKey(BusinessId: 1, CredentialRevision: 2);

        AssertFromRead(1, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(2, await cache.GetOrReadAsync(other, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(3, await cache.GetOrReadAsync(newerRevision, reader.ReadAsync, CancellationToken.None));

        // And each one keeps its own answer rather than the last one written.
        AssertFromRead(1, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        AssertFromRead(2, await cache.GetOrReadAsync(other, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    public async Task A_fresh_read_ignores_what_is_held_and_replaces_it()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
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
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [2]);
        var failure = new NayaxUpstreamException(
            "GetMachinesAsync", HttpMethod.Get, "machines", HttpStatusCode.BadGateway);

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
    /// wins": results arrive in whatever order the upstream answers them. A read that started before
    /// a fresh read must not store its older list over the fresher one just because it finished
    /// afterwards.
    /// </summary>
    [Fact]
    public async Task An_older_read_still_running_never_overwrites_a_newer_fresh_read()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var older = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the older read to start");

        var fresh = await cache.ReadFreshAsync(Key, reader.ReadAsync, CancellationToken.None);
        AssertFromRead(2, fresh);

        reader.Complete(1);

        // The caller that started first still gets the list it was answered with - it asked, and this
        // is the answer it got. What must not happen is that answer replacing the newer one.
        AssertFromRead(1, await older);
        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(2, reader.Calls);
    }

    /// <summary>
    /// The same rule when the older read is the one that finishes first. It was answered with, and it
    /// is still not what the next caller sees: the fresh read started after it, so the fresh read's
    /// list is the newer news whichever order the two arrive in.
    /// </summary>
    [Fact]
    public async Task An_older_read_that_finishes_first_is_still_not_the_answer_after_a_fresh_read()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
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

    /// <summary>
    /// And the rule holds when the fresh read brings back nothing at all. A fresh read that fails or
    /// is cancelled has no result to store, but it has already happened for this key - so the older
    /// read it started beside must not become the answer by default, which is what issue #840 means
    /// by an older in-flight result not being stored once a fresh read happens. The fence is taken
    /// when the fresh read begins, not when it succeeds.
    /// </summary>
    [Fact]
    public async Task A_failed_fresh_read_still_bars_the_older_read_it_started_beside()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1, 2]);
        var failure = new NayaxUpstreamException(
            "GetMachinesAsync", HttpMethod.Get, "machines", HttpStatusCode.BadGateway);

        var older = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the older read to start");

        var fresh = Task.Run(() => cache.ReadFreshAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(2), "the fresh read to start");
        reader.Fail(2, failure);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => fresh));

        reader.Complete(1);
        AssertFromRead(1, await older);

        // Nothing is held: the next caller reads Nayax rather than being handed the list of a read
        // the fresh read had already superseded.
        AssertFromRead(3, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    public async Task An_older_read_still_running_never_overwrites_an_invalidation()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
        var reader = new Reader(blockedCalls: [1]);

        var older = Task.Run(() => cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
        await WaitUntil(() => reader.Waiting(1), "the older read to start");

        cache.Invalidate(Key);
        reader.Complete(1);
        AssertFromRead(1, await older);

        // Nothing is held, so the next caller reads again rather than being handed a list the
        // invalidation already decided was not to be kept.
        AssertFromRead(2, await cache.GetOrReadAsync(Key, reader.ReadAsync, CancellationToken.None));
    }

    /// <summary>
    /// An invalidation has to reach the read that is still running, not only the list already held.
    /// A caller that arrives while that read is open would otherwise join it and be answered with
    /// exactly the pre-invalidation list - the one case the invalidation exists to prevent - and
    /// nothing would have been stored for a later test to notice.
    /// </summary>
    [Fact]
    public async Task A_caller_after_an_invalidation_does_not_join_the_read_that_was_already_running()
    {
        var cache = new NayaxMachineListCache(new FakeClock(Now));
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
    /// Keys are forgotten rather than accumulated, and only the ones holding nothing usable: the
    /// clean-up is about memory and must never make a live key answer differently.
    /// </summary>
    [Fact]
    public async Task Expired_keys_are_forgotten_once_too_many_are_remembered()
    {
        var clock = new FakeClock(Now);
        var cache = new NayaxMachineListCache(clock);
        var reader = new Reader();

        for (var business = 1; business <= NayaxMachineListCache.MaxTrackedKeys; business++)
        {
            await cache.GetOrReadAsync(
                new NayaxMachineListCacheKey(business, CredentialRevision: 1),
                reader.ReadAsync,
                CancellationToken.None);
        }

        Assert.Equal(NayaxMachineListCache.MaxTrackedKeys, cache.TrackedKeys);

        clock.UtcNow = Now.Add(NayaxMachineListCache.Lifetime);
        var survivor = new NayaxMachineListCacheKey(BusinessId: 9_000, CredentialRevision: 1);
        await cache.GetOrReadAsync(survivor, reader.ReadAsync, CancellationToken.None);

        Assert.True(
            cache.TrackedKeys <= NayaxMachineListCache.MaxTrackedKeys,
            $"Expected at most {NayaxMachineListCache.MaxTrackedKeys} tracked keys, found {cache.TrackedKeys}.");

        // The key that is still live kept its list: clearing out expired entries is not an
        // invalidation of everything.
        var callsBefore = reader.Calls;
        await cache.GetOrReadAsync(survivor, reader.ReadAsync, CancellationToken.None);
        Assert.Equal(callsBefore, reader.Calls);
    }

    private static void AssertFromRead(int read, List<NayaxMachine> machines) =>
        Assert.Equal($"read {read}", Assert.Single(machines).MachineName);

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
    /// A scripted stand-in for the machine-list read. Every call is numbered and counted, and a call
    /// named in <c>blockedCalls</c> waits until the test completes or fails it - which is what makes
    /// "two reads overlap" something a test can state rather than hope for.
    /// </summary>
    private sealed class Reader
    {
        private readonly HashSet<int> _blockedCalls;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<List<NayaxMachine>>> _gates = new();
        private int _calls;

        public Reader(params int[] blockedCalls) => _blockedCalls = [.. blockedCalls];

        public int Calls => Volatile.Read(ref _calls);

        public Task<List<NayaxMachine>> ReadAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            return _blockedCalls.Contains(call)
                ? Gate(call).Task.WaitAsync(cancellationToken)
                : Task.FromResult(Fleet(call));
        }

        /// <summary>Whether the numbered call has started and is waiting to be released.</summary>
        public bool Waiting(int call) => Calls >= call && _gates.ContainsKey(call);

        public void Complete(int call) => Gate(call).TrySetResult(Fleet(call));

        public void Fail(int call, Exception failure) => Gate(call).TrySetException(failure);

        private TaskCompletionSource<List<NayaxMachine>> Gate(int call) =>
            _gates.GetOrAdd(
                call,
                _ => new TaskCompletionSource<List<NayaxMachine>>(TaskCreationOptions.RunContinuationsAsynchronously));

        // One machine per read, named after the read it came from, so a test can say which read an
        // answer came from instead of only how many reads happened.
        private static List<NayaxMachine> Fleet(int call) =>
            [new NayaxMachine { MachineID = call, MachineName = $"read {call}" }];
    }
}
