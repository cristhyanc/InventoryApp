using Inventory.Application.Nayax;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Fake <see cref="INayaxLynxClient"/> for the Sites use cases (issue #313), serving a fixed machine
/// fleet and per-machine product list and recording how many machine-product requests were in flight
/// at once. Nayax requests are independent remote reads that share no scoped <c>AppDbContext</c>, so
/// their bounded fan-out must stay concurrent even though the facts-store reads are serialized.
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

    public int MaxConcurrentMachineProductCalls { get; private set; }

    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
        Task.FromResult(_machines);

    public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        MaxConcurrentMachineProductCalls = Math.Max(MaxConcurrentMachineProductCalls, inFlight);

        if (_expectedConcurrentMachineProductCalls > 0)
        {
            if (Interlocked.Increment(ref _started) >= _expectedConcurrentMachineProductCalls)
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

    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
}
