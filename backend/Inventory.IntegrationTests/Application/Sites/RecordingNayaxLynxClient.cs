using Inventory.Application.Nayax;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Fake <see cref="INayaxLynxClient"/> for the Sites use cases (issue #313), serving a fixed machine
/// fleet and per-machine product list and recording how many machine-product requests were in flight
/// at once. The bound on a Nayax fan-out belongs to the remote system, so it must stay concurrent
/// even though the facts-store reads are serialized - and even though the real client resolves the
/// business's credentials through the same scoped <c>AppDbContext</c> since issue #520, which
/// <c>EfNayaxConnectionStore</c> serializes for itself rather than by holding the remote requests
/// apart (issue #667). This double makes no such read; the relational proof that the real one stays
/// concurrent is <c>NayaxParallelCredentialAccessTests</c>.
/// When <c>expectedConcurrentMachineProductCalls</c> is set, each machine-product call waits until that
/// many calls have started (bounded by a timeout, so a serialized caller fails the assertion instead of
/// hanging the suite).
/// </summary>
public sealed class RecordingNayaxLynxClient : INayaxLynxClient
{
    private static readonly TimeSpan FanOutWait = TimeSpan.FromSeconds(10);

    private readonly List<NayaxMachine> _machines;
    private readonly IReadOnlyDictionary<long, List<NayaxMachineProduct>> _productsByMachine;
    private readonly int _expectedConcurrentMachineProductCalls;
    private readonly long? _failingMachineId;
    private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _inFlight;
    private int _maxInFlight;

    public RecordingNayaxLynxClient(
        IEnumerable<NayaxMachine> machines,
        IReadOnlyDictionary<long, List<NayaxMachineProduct>>? productsByMachine = null,
        int expectedConcurrentMachineProductCalls = 0,
        long? failingMachineId = null)
    {
        _machines = machines.ToList();
        _productsByMachine = productsByMachine ?? new Dictionary<long, List<NayaxMachineProduct>>();
        _expectedConcurrentMachineProductCalls = expectedConcurrentMachineProductCalls;
        _failingMachineId = failingMachineId;
    }

    /// <summary>
    /// The highest number of machine-product requests ever in flight at once. Maintained with
    /// interlocked operations rather than a plain read-modify-write, because a bounded fan-out
    /// (issue #845) has several requests updating it simultaneously and a lost update would under-report
    /// the concurrency a test is asserting on.
    /// </summary>
    public int MaxConcurrentMachineProductCalls => Volatile.Read(ref _maxInFlight);

    /// <summary>How many machine-product requests were made in total, however many ran at once.</summary>
    public int MachineProductCallCount => Volatile.Read(ref _started);

    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
        Task.FromResult(_machines);

    // This double caches nothing, so both reads answer with the same fleet (issue #840).
    public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
        Task.FromResult(_machines);

    public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default)
    {
        var started = Interlocked.Increment(ref _started);
        var inFlight = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _maxInFlight, inFlight);

        if (_expectedConcurrentMachineProductCalls > 0)
        {
            if (started >= _expectedConcurrentMachineProductCalls)
                _allStarted.TrySetResult();

            await Task.WhenAny(_allStarted.Task, Task.Delay(FanOutWait, ct));
        }
        else
        {
            await Task.Yield();
        }

        Interlocked.Decrement(ref _inFlight);

        if (_failingMachineId == machineId)
            throw new InvalidOperationException($"Nayax machine {machineId} is unavailable");

        return _productsByMachine.TryGetValue(machineId, out var products) ? products : [];
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int initial;
        do
        {
            initial = Volatile.Read(ref location);
            if (value <= initial) return;
        } while (Interlocked.CompareExchange(ref location, value, initial) != initial);
    }

    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
}
